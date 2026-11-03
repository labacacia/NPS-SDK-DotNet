// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.ActionNode;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

/// <summary>Resource-plane tests with trusted fixture principals, not NIP proof-of-possession qualification.</summary>
public sealed class ResultResourcePolicyTests
{
    private const string Issuer = "urn:nps:node:fixture:results";
    private const string Schema = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Digest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly ResultPrincipal Owner = new("tenant", "owner", "v1");
    private static readonly ResultPrincipal Worker = new("tenant", "worker", "v1");
    private static ResultSourceAuthority Authority => new("tenant", "owner", "orders", "v1",
        ["id", "reason", "amount"], ["result.create", "result.read", "result.aggregate", "result.release"],
        AllowDelegation: true);

    private static (ResultResourcePolicy Policy, ResultResourceStore Store) Setup(
        ResultSourceAuthority? authority = null, TestTime? time = null, int maxGrants = 4096)
    {
        var policy = new ResultResourcePolicy(Issuer, maxGrants: maxGrants, timeProvider: time);
        policy.SetSourceAuthority(authority ?? Authority);
        return (policy, new(new ResultStoreOptions { IssuerNid = Issuer }, policy.Authorize, time));
    }

    private static Task<ResultDescriptor> Create(ResultResourceStore store, ResultPrincipal? owner = null) =>
        store.CreateAsync(owner ?? Owner, "orders", Guid.NewGuid().ToString(), Digest,
            _ => ValueTask.FromResult(new ResultSnapshot("source-1", Schema,
                JsonSerializer.SerializeToElement(new[] { new { id = "a", reason = "delivery", amount = 1200 },
                    new { id = "b", reason = "delivery", amount = 300 } }))));

    private static ResultPage Read(ResultResourceStore store, ResultDescriptor result,
        ResultPrincipal? caller = null, string[]? fields = null, bool toModel = false) =>
        store.Read(caller ?? Worker, result.ResultRef, result.SnapshotId, result.SchemaRef,
            fields: fields ?? ["id"], toModel: toModel);

    private static ResultRecipientGrant Grant(ResultResourcePolicy policy, ResultResourceStore store,
        ResultDescriptor result, string[]? fields = null, string[]? operations = null, bool egress = false) =>
        policy.IssueGrant(Owner, store.GetOwnerGrantScope(Owner, result.ResultRef), "worker",
            operations ?? ["result.read"], fields ?? ["id"], result.ExpiresAt, egress);

    [Fact]
    public async Task MissingSourceAuthorityDeniesBeforeCapture()
    {
        var policy = new ResultResourcePolicy(Issuer);
        var store = new ResultResourceStore(new ResultStoreOptions { IssuerNid = Issuer }, policy.Authorize);
        var error = await Assert.ThrowsAsync<ActionNodeException>(() => Create(store));
        Assert.Equal("NWP-RESULT-NOT-FOUND", error.ErrorCode);
    }

    [Fact]
    public async Task GrantCannotAuthorizeAnotherResultWithTheSameSourceAndSnapshot()
    {
        var (policy, store) = Setup();
        var first = await Create(store);
        var second = await Create(store);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Grant(policy, store, first);
        Assert.Equal(2, Read(store, first).Rows.Count);
        Assert.Throws<ActionNodeException>(() => Read(store, second));
        Assert.Throws<ActionNodeException>(() => Read(store, first, Worker with { Tenant = "other-tenant" }));
    }

    [Fact]
    public async Task FieldAndOperationGrantsIntersectWithoutImplicitExpansion()
    {
        var (policy, store) = Setup();
        var result = await Create(store);
        Grant(policy, store, result);
        Assert.False(Read(store, result).Rows[0].TryGetProperty("amount", out _));
        Assert.Throws<ActionNodeException>(() => Read(store, result, fields: ["amount"]));
        Assert.Throws<ActionNodeException>(() => store.Aggregate(Worker, result.ResultRef,
            result.SnapshotId, Schema, ["reason"], "amount"));
        Assert.Throws<ActionNodeException>(() => store.Release(Worker, result.ResultRef));
        await Assert.ThrowsAsync<ActionNodeException>(() => Create(store, Worker));
    }

    [Fact]
    public async Task ExplicitAggregateGrantComputesOnlyItsVisibleFields()
    {
        var (policy, store) = Setup();
        var result = await Create(store);
        Grant(policy, store, result, ["reason", "amount"], ["result.aggregate"]);
        var aggregate = store.Aggregate(Worker, result.ResultRef, result.SnapshotId, Schema, ["reason"], "amount");
        Assert.Equal(1500, aggregate.Groups.Single().GetProperty("sum").GetInt64());
        Assert.Equal(2, aggregate.Groups.Single().GetProperty("count").GetInt64());
        Assert.Throws<ActionNodeException>(() => Read(store, result, fields: ["reason"]));
    }

    [Fact]
    public async Task RecipientCannotObtainAnOwnerScopeOrRecursivelyDelegate()
    {
        var (policy, store) = Setup();
        var result = await Create(store);
        var scope = store.GetOwnerGrantScope(Owner, result.ResultRef);
        Grant(policy, store, result, ["id", "reason", "amount"]);
        Assert.Equal(2, Read(store, result).Rows.Count);
        Assert.Throws<ActionNodeException>(() => store.GetOwnerGrantScope(Worker, result.ResultRef));
        Assert.Throws<ActionNodeException>(() => policy.IssueGrant(Worker, scope, "third",
            ["result.read"], ["id"], result.ExpiresAt));
    }

    [Fact]
    public async Task RevocationIsOwnerAndTenantBound()
    {
        var (policy, store) = Setup();
        var result = await Create(store);
        var grant = Grant(policy, store, result);
        Assert.False(policy.RevokeGrant(Worker, grant.GrantId));
        Assert.False(policy.RevokeGrant(Owner with { Tenant = "other" }, grant.GrantId));
        Assert.Equal(2, Read(store, result).Rows.Count);
        Assert.True(policy.RevokeGrant(Owner, grant.GrantId));
        Assert.False(policy.RevokeGrant(Owner, grant.GrantId));
        Assert.Throws<ActionNodeException>(() => Read(store, result));
    }

    [Fact]
    public async Task LiveSourceAndDelegationWithdrawalApplyToExistingGrants()
    {
        var (policy, store) = Setup();
        var result = await Create(store);
        Grant(policy, store, result);
        policy.SetSourceAuthority(Authority with { AllowDelegation = false });
        Assert.Throws<ActionNodeException>(() => Read(store, result));
        Assert.Equal(2, Read(store, result, Owner).Rows.Count);
        policy.SetSourceAuthority(Authority with { Enabled = false });
        Assert.Throws<ActionNodeException>(() => Read(store, result, Owner));
        await Assert.ThrowsAsync<ActionNodeException>(() => Create(store));
    }

    [Fact]
    public async Task ChangedRowPolicyRequiresExplicitRecaptureAndCannotReviveAnOldGrant()
    {
        var (policy, store) = Setup();
        var old = await Create(store);
        Grant(policy, store, old);
        policy.SetSourceAuthority(Authority with { PolicyVersion = "v2" });
        var updatedOwner = Owner with { PolicyVersion = "v2" };
        var updatedWorker = Worker with { PolicyVersion = "v2" };
        Assert.Throws<ActionNodeException>(() => Read(store, old, updatedOwner));
        Assert.Throws<ActionNodeException>(() => Read(store, old, updatedWorker));
        var captured = await Create(store, updatedOwner);
        Assert.Equal(2, Read(store, captured, updatedOwner).Rows.Count);
        Assert.Throws<ActionNodeException>(() => Read(store, captured, updatedWorker));
    }

    [Fact]
    public async Task ModelEgressNeedsBothLiveSourceAndExplicitRecipientPermission()
    {
        var (policy, store) = Setup(Authority with { AllowModelEgress = true });
        var result = await Create(store);
        var grant = Grant(policy, store, result);
        Assert.Throws<ActionNodeException>(() => Read(store, result, toModel: true));
        policy.RevokeGrant(Owner, grant.GrantId);
        Grant(policy, store, result, egress: true);
        Assert.Equal(2, Read(store, result, toModel: true).Rows.Count);
        policy.SetSourceAuthority(Authority with { AllowModelEgress = false });
        Assert.Equal(2, Read(store, result).Rows.Count);
        Assert.Throws<ActionNodeException>(() => Read(store, result, toModel: true));
    }

    [Fact]
    public async Task GrantCannotBroadenExpiryFieldsOperationsEgressOrIssuer()
    {
        var (policy, store) = Setup();
        var result = await Create(store);
        var scope = store.GetOwnerGrantScope(Owner, result.ResultRef);
        Assert.Throws<ActionNodeException>(() => policy.IssueGrant(Owner, scope, "worker",
            ["result.read"], ["id"], result.ExpiresAt + 1));
        Assert.Throws<ActionNodeException>(() => Grant(policy, store, result, fields: ["secret"]));
        Assert.Throws<ArgumentException>(() => Grant(policy, store, result, operations: ["result.release"]));
        Assert.Throws<ActionNodeException>(() => Grant(policy, store, result, egress: true));
        var otherIssuer = new ResultResourcePolicy("urn:nps:node:fixture:other");
        otherIssuer.SetSourceAuthority(Authority);
        Assert.Throws<ActionNodeException>(() => otherIssuer.IssueGrant(Owner, scope, "worker",
            ["result.read"], ["id"], result.ExpiresAt));
    }

    [Fact]
    public async Task GrantExpiryUsesMonotonicTimeDuringWallClockRollback()
    {
        var time = new TestTime();
        var (policy, store) = Setup(time: time);
        var result = await Create(store);
        policy.IssueGrant(Owner, store.GetOwnerGrantScope(Owner, result.ResultRef), "worker",
            ["result.read"], ["id"], time.GetUtcNow().ToUnixTimeSeconds() + 60);
        time.WallOffset = -1000;
        time.Advance(60);
        Assert.Throws<ActionNodeException>(() => Read(store, result));
        Assert.Equal(2, Read(store, result, Owner).Rows.Count);
    }

    [Fact]
    public async Task CapacityRejectsWithoutEvictingActiveGrantAndRevocationFreesIt()
    {
        var (policy, store) = Setup(maxGrants: 1);
        var result = await Create(store);
        var grant = Grant(policy, store, result);
        var error = Assert.Throws<ActionNodeException>(() => Grant(policy, store, result));
        Assert.Equal("NWP-RESULT-CAPACITY", error.ErrorCode);
        Assert.Equal(2, Read(store, result).Rows.Count);
        policy.RevokeGrant(Owner, grant.GrantId);
        Grant(policy, store, result);
        Assert.Equal(2, Read(store, result).Rows.Count);
    }

    [Fact]
    public async Task PolicyCopiesMutableAdministrationCollections()
    {
        var fields = new List<string> { "id", "reason", "amount" };
        var (policy, store) = Setup(Authority with { Fields = fields });
        fields.Clear();
        var result = await Create(store);
        var grantFields = new List<string> { "id" };
        policy.IssueGrant(Owner, store.GetOwnerGrantScope(Owner, result.ResultRef), "worker",
            ["result.read"], grantFields, result.ExpiresAt);
        grantFields.Clear();
        Assert.Equal(2, Read(store, result).Rows.Count);
    }

    [Fact]
    public void SourceAndPolicyByteLimitsCannotGrowWithoutBound()
    {
        var oneSource = new ResultResourcePolicy(Issuer, maxSources: 1);
        oneSource.SetSourceAuthority(Authority);
        Assert.Throws<ActionNodeException>(() => oneSource.SetSourceAuthority(Authority with { SourceId = "other" }));
        var tiny = new ResultResourcePolicy(Issuer, maxPolicyBytes: 1024);
        Assert.Throws<ActionNodeException>(() => tiny.SetSourceAuthority(Authority));
    }

    private sealed class TestTime : TimeProvider
    {
        private long _seconds;
        public long WallOffset { get; set; }
        public void Advance(long seconds) => _seconds += seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + _seconds + WallOffset);
        public override long GetTimestamp() => _seconds;
        public override long TimestampFrequency => 1;
    }
}
