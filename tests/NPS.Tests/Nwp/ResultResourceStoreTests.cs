// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.ActionNode;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

public sealed class ResultResourceStoreTests
{
    [Fact]
    public async Task CaptureByteCapIncludesExactUtf8EscapesAndArrayFramingWithoutPublishingRejectedKeys()
    {
        var snapshot = Snapshot(new[] {
            new { id = "quote\"é<script>", amount = 12, note = (string?)null },
            new { id = "newline\n", amount = 23, note = (string?)null },
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Rows).Length;
        var exact = Store(Options with { MaxResultBytes = bytes });
        var descriptor = await Create(exact, snapshot: snapshot);
        Assert.True(JsonElement.DeepEquals(snapshot.Rows, JsonSerializer.SerializeToElement(Read(exact, descriptor).Rows)));
        var replay = await Create(exact, snapshot: snapshot);
        Assert.Equal(descriptor, replay);
        var second = await Create(exact, key: Key2, snapshot: snapshot);
        Assert.Equal(descriptor.SnapshotId, second.SnapshotId);

        var below = Store(Options with { MaxResultBytes = bytes - 1 });
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(below, snapshot: snapshot));
        Assert.Equal("NWP-RESULT-CAPACITY", error.ErrorCode);
        // A failed capture never published/reserved this key or owner slot.
        var smaller = Snapshot(new[] { new { id = "small" } });
        var admitted = await Create(below, snapshot: smaller);
        Assert.Equal("small", Read(below, admitted).Rows[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task RowWiseInspectionOwnsNestedPagesAndGroupedScalarKeysAfterRelease()
    {
        var store = new ResultResourceStore(Options, (_, _) => true);
        var captured = await store.CreateAsync(Alice, "orders", Key, Digest, _ => ValueTask.FromResult(Snapshot(new[]
        {
            new { category = "é", active = true, amount = 2, nested = new { id = "row-a", list = new[] { 1, 2 } } },
            new { category = "é", active = true, amount = 3, nested = new { id = "row-b", list = new[] { 3, 4 } } },
            new { category = "quote\"x", active = false, amount = 7, nested = new { id = "row-c", list = new[] { 5, 6 } } },
        })));
        var first = store.Read(Alice, captured.ResultRef, captured.SnapshotId, Schema, 1, ["nested"]);
        var rest = store.Read(Alice, captured.ResultRef, captured.SnapshotId, Schema, 2, ["nested"], first.NextCursor);
        var grouped = store.Aggregate(Alice, captured.ResultRef, captured.SnapshotId, Schema, ["category", "active"], "amount");
        store.Release(Alice, captured.ResultRef);
        Assert.Equal("row-a", first.Rows[0].GetProperty("nested").GetProperty("id").GetString());
        Assert.Equal(new[] { "row-b", "row-c" }, rest.Rows.Select(row => row.GetProperty("nested").GetProperty("id").GetString()));
        Assert.Null(rest.NextCursor);
        var combined = grouped.Groups.Single(group => group.GetProperty("keys")[0].GetString() == "é");
        Assert.True(combined.GetProperty("keys")[1].GetBoolean());
        Assert.Equal(2, combined.GetProperty("count").GetInt32());
        Assert.Equal(5, combined.GetProperty("sum").GetInt64());
    }

    private static readonly ResultPrincipal Alice = new("tenant-a", "urn:nps:agent:fixture:alice", "policy-1");
    private static readonly ResultPrincipal Bob = new("tenant-a", "urn:nps:agent:fixture:bob", "policy-1");
    private const string Schema = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Digest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Key = "00000000-0000-4000-8000-000000000001";
    private const string Key2 = "00000000-0000-4000-8000-000000000002";

    private static ResultStoreOptions Options => new() { IssuerNid = "urn:nps:node:fixture:results" };

    private static ResultSnapshot Snapshot(object? rows = null, string version = "source-1") =>
        new(version, Schema, JsonSerializer.SerializeToElement(rows ?? new[]
        {
            new { id = "one", reason = "a", amount = 12 },
            new { id = "two", reason = "a", amount = 23 },
            new { id = "three", reason = "b", amount = 9 },
        }));

    private static ResultResourceStore Store(ResultStoreOptions? options = null, TimeProvider? time = null,
        Func<ResultPrincipal, ResultAccess, bool>? policy = null) =>
        new(options ?? Options, policy ?? ((caller, access) => caller.SubjectNid == access.OwnerSubject), time);

    private static Task<ResultDescriptor> Create(ResultResourceStore store, ResultPrincipal? caller = null,
        string key = Key, string digest = Digest, ResultSnapshot? snapshot = null,
        int? ttl = null, CancellationToken ct = default) =>
        store.CreateAsync(caller ?? Alice, "orders", key, digest,
            _ => ValueTask.FromResult(snapshot ?? Snapshot()), ttl, ct);

    private static ResultPage Read(ResultResourceStore store, ResultDescriptor descriptor,
        ResultPrincipal? caller = null, int limit = 20, IReadOnlyList<string>? fields = null,
        string? cursor = null, bool toModel = false) =>
        store.Read(caller ?? Alice, descriptor.ResultRef, descriptor.SnapshotId, descriptor.SchemaRef,
            limit, fields, cursor, toModel);

    [Fact]
    public async Task CaptureIsImmutableAfterSourceDocumentIsDisposed()
    {
        var store = Store();
        var document = JsonDocument.Parse("""[{"id":"original","amount":7}]""");
        var created = await Create(store, snapshot: new("v1", Schema, document.RootElement));
        document.Dispose();
        Assert.Equal("original", Read(store, created).Rows[0].GetProperty("id").GetString());
        Assert.Equal("process", created.Retention);
        Assert.DoesNotContain("rows", JsonSerializer.Serialize(created));
        Assert.Equal(64, created.ResultRef.Length);
    }

    [Fact]
    public async Task SameKeyReplaysOnlyAfterCurrentPolicyChecks()
    {
        var allow = true;
        var store = Store(policy: (_, _) => allow);
        var first = await Create(store);
        var replay = await Create(store, snapshot: Snapshot(version: "changed-live-source"));
        Assert.Equal(first, replay);
        allow = false;
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store));
        Assert.Equal("NWP-RESULT-NOT-FOUND", error.ErrorCode);
    }

    [Fact]
    public async Task SameKeyWithChangedRequestDigestConflicts()
    {
        var store = Store();
        await Create(store);
        var changed = "sha256:" + new string('3', 64);
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store, digest: changed));
        Assert.Equal("NWP-RESULT-REPLAY-CONFLICT", error.ErrorCode);
    }

    [Fact]
    public async Task ChangingSourceCannotCreateAnotherResultUnderTheSameOwnerKey()
    {
        var store = Store();
        await Create(store);
        var captures = 0;
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => store.CreateAsync(Alice,
            "different-source", Key, "sha256:" + new string('3', 64), _ =>
            {
                captures++;
                return ValueTask.FromResult(Snapshot());
            }));
        Assert.Equal("NWP-RESULT-REPLAY-CONFLICT", error.ErrorCode);
        Assert.Equal(0, captures);
    }

    [Fact]
    public async Task ConcurrentSameKeyCapturesPublishOneResult()
    {
        var store = Store(Options with { MaxResultsPerOwner = 1 });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async ValueTask<ResultSnapshot> Capture(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 2) ready.SetResult();
            await ready.Task.WaitAsync(ct);
            return Snapshot();
        }
        var requests = Enumerable.Range(0, 2).Select(_ =>
            store.CreateAsync(Alice, "orders", Key, Digest, Capture));
        var results = await Task.WhenAll(requests);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(3, Read(store, results[0]).Rows.Count);
    }

    [Fact]
    public async Task PageCursorTraversesOnlyItsSnapshot()
    {
        var store = Store();
        var created = await Create(store);
        var first = Read(store, created, limit: 2);
        var last = Read(store, created, limit: 2, cursor: first.NextCursor);
        Assert.Equal(new[] { "one", "two", "three" }, first.Rows.Concat(last.Rows)
            .Select(row => row.GetProperty("id").GetString()));
        Assert.Null(last.NextCursor);
        var newer = await Create(store, key: Key2, snapshot: Snapshot(version: "v2"));
        var error = Assert.Throws<ActionNodeException>(() =>
            Read(store, newer, cursor: first.NextCursor));
        Assert.Equal("NWP-RESULT-SNAPSHOT-MISMATCH", error.ErrorCode);
        Assert.NotEqual(created.SnapshotId, newer.SnapshotId);
    }

    [Fact]
    public async Task ChangedFieldsOrPolicyInvalidatesCursor()
    {
        var store = Store();
        var created = await Create(store);
        var cursor = Read(store, created, limit: 1).NextCursor;
        Assert.Throws<ActionNodeException>(() => Read(store, created, fields: ["id"], cursor: cursor));
        var changedPolicy = Alice with { PolicyVersion = "policy-2" };
        Assert.Throws<ActionNodeException>(() => Read(store, created, changedPolicy, cursor: cursor));
    }

    [Fact]
    public async Task TamperedCursorCannotSelectAnotherOffset()
    {
        var store = Store();
        var created = await Create(store);
        var cursor = Read(store, created, limit: 1).NextCursor!;
        var altered = (cursor[0] == 'A' ? "B" : "A") + cursor[1..];
        var error = Assert.Throws<ActionNodeException>(() => Read(store, created, cursor: altered));
        Assert.Equal("NWP-RESULT-SNAPSHOT-MISMATCH", error.ErrorCode);
    }

    [Fact]
    public async Task CopiedHandleAndWrongTenantHaveSameNotFoundError()
    {
        var store = Store();
        var created = await Create(store);
        var denied = Assert.Throws<ActionNodeException>(() => Read(store, created, Bob));
        var wrongTenant = Assert.Throws<ActionNodeException>(() =>
            Read(store, created, Alice with { Tenant = "tenant-b" }));
        var absent = Assert.Throws<ActionNodeException>(() =>
            store.Read(Alice, "absent", "unknown", Schema));
        Assert.Equal("NWP-RESULT-NOT-FOUND", denied.ErrorCode);
        Assert.Equal(denied.ErrorCode, wrongTenant.ErrorCode);
        Assert.Equal(denied.Message, absent.Message);
    }

    [Fact]
    public async Task RecipientGrantStillRequiresLiveSourcePolicy()
    {
        var sourceAllowed = true;
        var store = Store(policy: (caller, access) => sourceAllowed &&
            (caller.SubjectNid == access.OwnerSubject ||
             caller == Bob && access.ActionId == "result.read"));
        var created = await Create(store);
        Assert.Equal(3, Read(store, created, Bob).Rows.Count);
        sourceAllowed = false;
        Assert.Throws<ActionNodeException>(() => Read(store, created, Bob));
        Assert.Throws<ActionNodeException>(() => Read(store, created));
    }

    [Fact]
    public async Task ModelEgressNeedsSeparateExplicitPermission()
    {
        var allowModel = false;
        var store = Store(policy: (caller, access) => caller.SubjectNid == access.OwnerSubject &&
            (!access.ToModel || allowModel));
        var created = await Create(store);
        Assert.Equal(3, Read(store, created).Rows.Count);
        Assert.Throws<ActionNodeException>(() => Read(store, created, toModel: true));
        allowModel = true;
        Assert.Equal(3, Read(store, created, toModel: true).Rows.Count);
    }

    [Fact]
    public async Task FieldNarrowingDoesNotBypassCurrentPolicy()
    {
        var hidden = false;
        var store = Store(policy: (_, access) => !hidden || !access.Fields.Contains("amount"));
        var created = await Create(store);
        hidden = true;
        Assert.Throws<ActionNodeException>(() => Read(store, created));
        var page = Read(store, created, fields: ["id"]);
        Assert.False(page.Rows[0].TryGetProperty("amount", out _));
        var replay = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store));
        Assert.Equal("NWP-RESULT-NOT-FOUND", replay.ErrorCode);
    }

    [Fact]
    public async Task ReleaseIsOwnerBoundIdempotentAndRemovesPayloadAccess()
    {
        var store = Store(policy: (_, _) => true);
        var created = await Create(store);
        Assert.Throws<ActionNodeException>(() => store.Release(Bob, created.ResultRef));
        Assert.True(store.Release(Alice, created.ResultRef).Released);
        Assert.True(store.Release(Alice, created.ResultRef).Released);
        Assert.Throws<ActionNodeException>(() => Read(store, created));
        await Assert.ThrowsAsync<ActionNodeException>(() => Create(store));
    }

    [Fact]
    public async Task ExpiryBoundaryAndWallRollbackNeverExtendTtl()
    {
        var time = new ControlledTime();
        var store = Store(time: time);
        var created = await Create(store, ttl: 5);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(Read(store, created, limit: 1).Rows);
        time.RewindWall(TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var error = Assert.Throws<ActionNodeException>(() => Read(store, created));
        Assert.Equal("NWP-RESULT-NOT-FOUND", error.ErrorCode);
    }

    [Fact]
    public async Task RestartDoesNotPretendToRecoverAnOldResult()
    {
        var first = Store();
        var created = await Create(first);
        var restarted = Store();
        Assert.Throws<ActionNodeException>(() => Read(restarted, created));
    }

    [Fact]
    public async Task OwnerAndGlobalCapacityRejectBeforePublishingAnotherResult()
    {
        var store = Store(Options with { MaxResultsPerOwner = 1 });
        var first = await Create(store);
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store, key: Key2));
        Assert.Equal("NWP-RESULT-CAPACITY", error.ErrorCode);
        Assert.Equal(3, Read(store, first).Rows.Count);
        var metadataOnly = Store(Options with { MaxStoreBytes = 3 });
        await Assert.ThrowsAsync<ActionNodeException>(() =>
            Create(metadataOnly, snapshot: Snapshot(Array.Empty<object>())));
    }

    [Fact]
    public async Task ReleaseMetadataIsBoundedInsteadOfEvictingLivePromises()
    {
        var store = Store(Options with { MaxRecords = 1 });
        var first = await Create(store);
        store.Release(Alice, first.ResultRef);
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store, key: Key2));
        Assert.Equal("NWP-RESULT-CAPACITY", error.ErrorCode);
    }

    [Fact]
    public async Task ExactCountAndIntegerSumKeepSourceProvenance()
    {
        var store = Store();
        var created = await Create(store);
        var aggregate = store.Aggregate(Alice, created.ResultRef, created.SnapshotId,
            Schema, ["reason"], "amount");
        Assert.Equal(created, aggregate.Descriptor);
        Assert.Equal(new long[] { 2, 1 }, aggregate.Groups.Select(group =>
            group.GetProperty("count").GetInt64()));
        Assert.Equal(new long[] { 35, 9 }, aggregate.Groups.Select(group =>
            group.GetProperty("sum").GetInt64()));
    }

    [Fact]
    public async Task SumOverflowIsAnExplicitProtocolError()
    {
        var store = Store();
        var snapshot = Snapshot(new[] { new { group = "a", amount = long.MaxValue },
                                         new { group = "a", amount = 1L } });
        var created = await Create(store, snapshot: snapshot);
        var error = Assert.Throws<ActionNodeException>(() => store.Aggregate(Alice,
            created.ResultRef, created.SnapshotId, Schema, ["group"], "amount"));
        Assert.Equal("NWP-ACTION-PARAMS-INVALID", error.ErrorCode);
    }

    [Fact]
    public async Task RowAndPageLimitsFailExplicitly()
    {
        var store = Store();
        var created = await Create(store);
        Assert.Throws<ActionNodeException>(() => Read(store, created, limit: 0));
        Assert.Throws<ActionNodeException>(() => Read(store, created, limit: 1001));
        var largeRow = await Create(store, key: Key2,
            snapshot: Snapshot(new[] { new { id = "large", value = new string('x', 66000) } }));
        Assert.Throws<ActionNodeException>(() => Read(store, largeRow));
        var smallStore = Store(Options with { MaxRowsPerResult = 1 });
        await Assert.ThrowsAsync<ActionNodeException>(() => Create(smallStore));
    }

    [Fact]
    public async Task PagesStayByteBoundedAndDoNotDropRecords()
    {
        var store = Store();
        var snapshot = Snapshot(Enumerable.Range(0, 10).Select(index =>
            new { id = index.ToString(), value = new string('x', 20000) }).ToArray());
        var created = await Create(store, snapshot: snapshot);
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = Read(store, created, limit: 1000, cursor: cursor);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page).Length <= 65536);
            ids.AddRange(page.Rows.Select(row => row.GetProperty("id").GetString()!));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(Enumerable.Range(0, 10).Select(index => index.ToString()), ids);
    }

    [Fact]
    public async Task DeniedAndCancelledCreateNeverInvokesTheSource()
    {
        var calls = 0;
        ValueTask<ResultSnapshot> Capture(CancellationToken ct)
        { Interlocked.Increment(ref calls); return ValueTask.FromResult(Snapshot()); }
        var denied = Store(policy: (_, _) => false);
        await Assert.ThrowsAsync<ActionNodeException>(() =>
            denied.CreateAsync(Alice, "orders", Key, Digest, Capture));
        var cancelled = Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelled.CreateAsync(Alice, "orders", Key, Digest, Capture, ct: cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationDuringAuthorizationStopsInspectionBeforeRowsAreReturned()
    {
        using var cancellation = new CancellationTokenSource();
        var store = Store(policy: (_, access) =>
        {
            if (access.ActionId is "result.read" or "result.aggregate") cancellation.Cancel();
            return true;
        });
        var created = await Create(store);
        Assert.ThrowsAny<OperationCanceledException>(() => store.Read(Alice, created.ResultRef,
            created.SnapshotId, Schema, ct: cancellation.Token));
        using var second = new CancellationTokenSource();
        var other = Store(policy: (_, access) =>
        {
            if (access.ActionId == "result.aggregate") second.Cancel();
            return true;
        });
        var captured = await Create(other);
        Assert.ThrowsAny<OperationCanceledException>(() => other.Aggregate(Alice, captured.ResultRef,
            captured.SnapshotId, Schema, [], ct: second.Token));
    }

    [Fact]
    public async Task PolicyFailureCannotFallbackToTheCallerHint()
    {
        var store = Store(policy: (_, _) => throw new InvalidOperationException("private policy detail"));
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store));
        Assert.Equal("NWP-RESULT-POLICY-UNAVAILABLE", error.ErrorCode);
        Assert.DoesNotContain("private", error.Message);
    }

    private sealed class ControlledTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan delta) { _now += delta; _timestamp += delta.Ticks; }
        public void RewindWall(TimeSpan delta) => _now -= delta;
    }

    [Fact]
    public async Task EmptyUngroupedCountIsZeroRatherThanAMissingGroup()
    {
        var store = Store();
        var created = await Create(store, snapshot: Snapshot(Array.Empty<object>()));
        var result = store.Aggregate(Alice, created.ResultRef, created.SnapshotId, Schema, []);
        Assert.Equal(0, Assert.Single(result.Groups).GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task DuplicateDecodedRowFieldsAreRejected()
    {
        var store = Store();
        using var document = JsonDocument.Parse("""[{"id":"one","id":"two"}]""");
        var error = await Assert.ThrowsAsync<ActionNodeException>(() =>
            Create(store, snapshot: new ResultSnapshot("v1", Schema, document.RootElement)));
        Assert.Equal("NWP-ACTION-PARAMS-INVALID", error.ErrorCode);
    }

    [Fact]
    public async Task CaptureFailuresDoNotExposeSourceDetailsOrPublishAResult()
    {
        var store = Store(Options with { MaxRecords = 1 });
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => store.CreateAsync(
            Alice, "orders", Key, Digest, _ => throw new InvalidOperationException("private source secret")));
        Assert.Equal("NWP-NODE-UNAVAILABLE", error.ErrorCode);
        Assert.DoesNotContain("secret", error.Message);
        Assert.NotNull(await Create(store, key: Key2));
    }

    [Fact]
    public async Task RevocationDuringCapturePreventsPublication()
    {
        var allow = true;
        var store = Store(Options with { MaxRecords = 1 }, policy: (_, _) => allow);
        ValueTask<ResultSnapshot> Capture(CancellationToken ct)
        { allow = false; return ValueTask.FromResult(Snapshot()); }
        await Assert.ThrowsAsync<ActionNodeException>(() =>
            store.CreateAsync(Alice, "orders", Key, Digest, Capture));
        allow = true;
        Assert.NotNull(await Create(store, key: Key2));
    }

}
