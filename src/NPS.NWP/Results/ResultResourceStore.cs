// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Core;
using NPS.NWP.ActionNode;

namespace NPS.NWP.Results;

/// <summary>
/// Bounded process-local snapshot storage for the experimental data-result contract.
/// Hosts must authenticate ResultPrincipal and consistently capture authorized source rows.
/// This class does not register or advertise a native/HTTP endpoint and is not a NIP verifier.
/// </summary>
public sealed class ResultResourceStore
{
    private sealed record Cursor(string Result, string Snapshot, string Schema,
        string Tenant, string Subject, string Policy, string Fields, int Offset);

    private sealed class Entry
    {
        public required ResultDescriptor Descriptor { get; init; }
        public required ResultPrincipal Owner { get; init; }
        public required string SourceId { get; init; }
        public required string SourceVersion { get; init; }
        public required string Key { get; init; }
        public required string RequestDigest { get; init; }
        public required string[] Fields { get; init; }
        public required long Timestamp { get; init; }
        public required int Ttl { get; init; }
        public required int MetadataBytes { get; init; }
        public required byte[] Payload { get; set; }
        public bool Released { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Subject, string Key), string> _keys = new();
    private readonly ResultStoreOptions _options;
    private readonly Func<ResultPrincipal, ResultAccess, bool> _policy;
    private readonly TimeProvider _time;
    private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
    private long _retainedBytes;
    private long _latestWallTime;

    /// <summary>
    /// Create an isolated process store with a mandatory, deployment-owned live policy.
    /// Policy must authorize source, owner/recipient grant, every field and model egress;
    /// caller-controlled values must never be substituted for its trusted inputs.
    /// </summary>
    public ResultResourceStore(ResultStoreOptions options,
        Func<ResultPrincipal, ResultAccess, bool> policy, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IssuerNid);
        if (Encoding.UTF8.GetByteCount(options.IssuerNid) > 512)
            throw new ArgumentOutOfRangeException(nameof(options), "Issuer identity exceeds bounds.");
        if (options.MaxResultsPerOwner is < 1 or > 16
            || options.MaxRowsPerResult is < 1 or > 100_000
            || options.MaxResultBytes is < 1 or > 33_554_432
            || options.MaxStoreBytes is < 1 or > 134_217_728
            || options.MaxRecords is < 1 or > 4096
            || options.MaxTtlSeconds is < 1 or > 900
            || options.DefaultTtlSeconds < 1 || options.DefaultTtlSeconds > options.MaxTtlSeconds)
            throw new ArgumentOutOfRangeException(nameof(options), "Limits exceed the frozen reference bounds.");
        _options = options;
        _policy = policy;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Authorize, capture and publish a finite immutable snapshot. The digest must be computed
    /// server-side from the frozen logical request using the existing canonicalization rules;
    /// it is never trusted directly from a client. Concurrent same-key requests may read the
    /// source twice but atomically publish one descriptor, with no external mutation.
    /// </summary>
    public async Task<ResultDescriptor> CreateAsync(ResultPrincipal caller, string sourceId,
        string idempotencyKey, string requestDigest,
        Func<CancellationToken, ValueTask<ResultSnapshot>> capture,
        int? ttlSeconds = null, CancellationToken ct = default, bool toModel = false)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var ttl = ValidateCreateMetadata(caller, sourceId, idempotencyKey, requestDigest, ttlSeconds, toModel);
        var key = (caller.Tenant, caller.SubjectNid, idempotencyKey);
        lock (_gate)
        {
            RemoveExpired();
            if (TryReplay(caller, key, requestDigest, out var replay, toModel)) return replay!;
            EnsureCreateSlot(caller);
        }

        ct.ThrowIfCancellationRequested();
        ResultSnapshot snapshot;
        try { snapshot = await capture(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new ActionNodeException(503, NpsStatusCodes.ServerUnavailable,
            "NWP-NODE-UNAVAILABLE", "Snapshot capture unavailable."); }
        if (!ValidDigest(snapshot.SchemaRef) || string.IsNullOrWhiteSpace(snapshot.SourceVersion)
            || Encoding.UTF8.GetByteCount(snapshot.SourceVersion) > 512
            || snapshot.Rows.ValueKind != JsonValueKind.Array)
            throw Invalid();
        var count = snapshot.Rows.GetArrayLength();
        if (count > _options.MaxRowsPerResult) throw Capacity();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var rowFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in snapshot.Rows.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (row.ValueKind != JsonValueKind.Object) throw Invalid();
            rowFields.Clear();
            foreach (var property in row.EnumerateObject())
            {
                var name = property.Name;
                if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > 128
                    || !rowFields.Add(name)) throw Invalid();
                fields.Add(name);
                if (fields.Count > 256) throw Invalid();
            }
        }
        var bytes = CopyRows(snapshot.Rows, ct);
        var allFields = fields.Order(StringComparer.Ordinal).ToArray();
        Authorize(caller, "result.create", sourceId, caller.SubjectNid, allFields, toModel);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            RemoveExpired();
            Authorize(caller, "result.create", sourceId, caller.SubjectNid, allFields, toModel);
            if (TryReplay(caller, key, requestDigest, out var replay, toModel)) return replay!;
            var created = Now();
            var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var snapshotId = Convert.ToHexStringLower(SHA256.HashData(bytes));
            // Snapshot hash also binds source revision/schema, not just coincidentally identical rows.
            snapshotId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                _options.IssuerNid + "\n" + sourceId + "\n" + snapshot.SchemaRef
                + "\n" + snapshot.SourceVersion + "\n" + snapshotId)));
            var descriptor = new ResultDescriptor(reference, _options.IssuerNid, snapshotId,
                snapshot.SchemaRef, count, created, checked(created + ttl), "process");
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                descriptor, caller, sourceId, snapshot.SourceVersion, idempotencyKey,
                requestDigest, fields = allFields,
            }).Length + 256;
            if (_entries.Count >= _options.MaxRecords
                || _retainedBytes + bytes.Length + metadataBytes > _options.MaxStoreBytes
                || _entries.Values.Count(entry => !entry.Released
                    && entry.Owner.Tenant == caller.Tenant
                    && entry.Owner.SubjectNid == caller.SubjectNid) >= _options.MaxResultsPerOwner)
                throw Capacity();
            _entries.Add(reference, new Entry { Descriptor = descriptor, Owner = caller,
                SourceId = sourceId, SourceVersion = snapshot.SourceVersion,
                Key = idempotencyKey, RequestDigest = requestDigest, Fields = allFields,
                Timestamp = _time.GetTimestamp(), Ttl = ttl, MetadataBytes = metadataBytes, Payload = bytes });
            _keys.Add(key, reference);
            _retainedBytes += bytes.Length + metadataBytes;
            return descriptor;
        }
    }

    // Own the temporary writer buffer rather than leaving whole-dataset buffers in a
    // shared serializer pool after repeated captures on different worker threads.
    // Retained bytes remain an exact copy; array framing counts against the same cap.
    private byte[] CopyRows(JsonElement rows, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(4096, _options.MaxResultBytes));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 64 }))
        {
            writer.WriteStartArray();
            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                row.WriteTo(writer);
                writer.Flush();
                if (buffer.WrittenCount > _options.MaxResultBytes) throw Capacity();
            }
            writer.WriteEndArray();
            writer.Flush();
        }
        if (buffer.WrittenCount > _options.MaxResultBytes) throw Capacity();
        return buffer.WrittenSpan.ToArray();
    }

    // Admission never captures/releases data or reserves a slot. Execution repeats the
    // same live checks and atomically enforces actual byte/row capacity at publication.
    internal void ValidateCreateAccess(ResultPrincipal caller, string sourceId, string key, string digest,
        int? ttl, bool toModel)
    {
        _ = ValidateCreateMetadata(caller, sourceId, key, digest, ttl, toModel);
        lock (_gate)
        {
            RemoveExpired();
            if (TryReplay(caller, (caller.Tenant, caller.SubjectNid, key), digest, out _, toModel)) return;
            EnsureCreateSlot(caller);
        }
    }

    internal void ValidateReadAccess(ResultPrincipal caller, string reference, string snapshot, string schema,
        int limit, IReadOnlyList<string>? fields, string? cursor, bool toModel, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw Invalid();
        // A single-row bounded probe also rejects an unreturnable first row before
        // dispatch. It does not advance server state/cursors or return data externally.
        _ = Read(caller, reference, snapshot, schema, 1, fields, cursor, toModel, ct);
    }

    internal void ValidateAggregateAccess(ResultPrincipal caller, string reference, string snapshot, string schema,
        IReadOnlyList<string> grouping, string? sumField, bool toModel)
    {
        lock (_gate) _ = PrepareAggregate(caller, reference, snapshot, schema, grouping, sumField, toModel);
    }

    internal void ValidateReleaseAccess(ResultPrincipal caller, string reference, bool toModel)
    {
        lock (_gate)
        {
            var entry = FindAuthorized(caller, reference, "result.release", [], toModel, allowReleased: true);
            if (entry.Owner.SubjectNid != caller.SubjectNid) throw Unavailable();
        }
    }

    private int ValidateCreateMetadata(ResultPrincipal caller, string sourceId, string key, string digest,
        int? ttlSeconds, bool toModel)
    {
        ValidatePrincipal(caller);
        if (string.IsNullOrWhiteSpace(sourceId) || Encoding.UTF8.GetByteCount(sourceId) > 128
            || !Guid.TryParseExact(key, "D", out var operationId) || operationId.ToString("D")[14] != '4'
            || !ValidDigest(digest)) throw Invalid();
        var ttl = ttlSeconds ?? _options.DefaultTtlSeconds;
        if (ttl < 1 || ttl > _options.MaxTtlSeconds) throw Invalid();
        Authorize(caller, "result.create", sourceId, caller.SubjectNid, [], toModel);
        return ttl;
    }

    private void EnsureCreateSlot(ResultPrincipal caller)
    {
        if (_entries.Count >= _options.MaxRecords || _retainedBytes >= _options.MaxStoreBytes
            || _entries.Values.Count(entry => !entry.Released && entry.Owner.Tenant == caller.Tenant
                && entry.Owner.SubjectNid == caller.SubjectNid) >= _options.MaxResultsPerOwner) throw Capacity();
    }

    /// <summary>
    /// Read one authorized page from an exact snapshot/schema. Optional projection narrows
    /// fields; cursor changes, cross-principal reuse and policy-version changes conflict.
    /// Every read, including model inspection, is independently authorized.
    /// </summary>
    public ResultPage Read(ResultPrincipal caller, string reference, string snapshotId,
        string schemaRef, int limit = 20, IReadOnlyList<string>? fields = null,
        string? cursor = null, bool toModel = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = FindAuthorized(caller, reference, "result.read", fields, toModel);
            if (limit is < 1 or > 1000) throw Invalid();
            EnsureSnapshot(entry, snapshotId, schemaRef);
            var selected = SelectFields(entry, fields);
            var offset = cursor is null ? 0 : ReadCursor(cursor, entry, caller, selected);
            if (offset > entry.Descriptor.RowCount) throw Conflict();
            // Walk the retained array without allocating metadata for every source row.
            // Only the current row is materialized; output pages own their serialized values.
            var reader = new Utf8JsonReader(entry.Payload);
            _ = reader.Read(); // Captured payload is a validated JSON array.
            var rows = new List<JsonElement>();
            ResultPage? accepted = null;
            var index = -1;
            while (rows.Count < limit && reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                if (index < offset) { reader.Skip(); continue; }
                using var rowDocument = JsonDocument.ParseValue(ref reader);
                var raw = rowDocument.RootElement;
                var projected = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var field in selected)
                    if (raw.TryGetProperty(field, out var value)) projected.Add(field, value);
                rows.Add(JsonSerializer.SerializeToElement(projected));
                var next = index + 1 < entry.Descriptor.RowCount
                    ? WriteCursor(entry, caller, selected, index + 1) : null;
                var candidate = new ResultPage(entry.Descriptor, rows.ToArray(), next);
                if (JsonSerializer.SerializeToUtf8Bytes(candidate).Length > 65536)
                {
                    if (accepted is null) throw Invalid();
                    break;
                }
                accepted = candidate;
            }
            return accepted ?? new ResultPage(entry.Descriptor, [], null);
        }
    }

    /// <summary>
    /// Compute exact COUNT and optional Int64 SUM over captured rows, grouped by up to four
    /// visible scalar fields. This bounded supported subset does not run caller SQL/scripts.
    /// Overflow, non-integer sum values or more than 1000 groups fail explicitly.
    /// </summary>
    public ResultAggregate Aggregate(ResultPrincipal caller, string reference, string snapshotId,
        string schemaRef, IReadOnlyList<string> groupBy, string? sumField = null, bool toModel = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = PrepareAggregate(caller, reference, snapshotId, schemaRef, groupBy, sumField, toModel);
            var reader = new Utf8JsonReader(entry.Payload);
            _ = reader.Read(); // Source capture already validated the finite array.
            var groups = new Dictionary<string, (JsonElement[] Keys, long Count, long Sum)>(StringComparer.Ordinal);
            if (groupBy.Count == 0) groups.Add("[]", ([], 0, 0));
            try
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    ct.ThrowIfCancellationRequested();
                    using var rowDocument = JsonDocument.ParseValue(ref reader);
                    var row = rowDocument.RootElement;
                    var keys = groupBy.Select(field => row.GetProperty(field)).ToArray();
                    if (keys.Any(value => value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
                        throw Invalid();
                    var key = JsonSerializer.Serialize(keys);
                    if (Encoding.UTF8.GetByteCount(key) > 4096) throw Invalid();
                    if (!groups.TryGetValue(key, out var current))
                    {
                        if (groups.Count >= 1000) throw Capacity();
                        // Keep independent scalar keys only once per bounded distinct group.
                        current = (keys.Select(value => value.Clone()).ToArray(), 0, 0);
                    }
                    var amount = sumField is null ? 0 : row.GetProperty(sumField).GetInt64();
                    groups[key] = (current.Keys, checked(current.Count + 1), checked(current.Sum + amount));
                }
            }
            catch (Exception error) when (error is OverflowException or KeyNotFoundException
                                          or InvalidOperationException or FormatException)
            { throw Invalid(); }
            var result = groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                var value = new Dictionary<string, object?> { ["keys"] = pair.Value.Keys,
                    ["count"] = pair.Value.Count };
                if (sumField is not null) value.Add("sum", pair.Value.Sum);
                return JsonSerializer.SerializeToElement(value);
            }).ToArray();
            var aggregate = new ResultAggregate(entry.Descriptor, result);
            if (JsonSerializer.SerializeToUtf8Bytes(aggregate).Length > 65536) throw Capacity();
            return aggregate;
        }
    }

    private Entry PrepareAggregate(ResultPrincipal caller, string reference, string snapshot, string schema,
        IReadOnlyList<string> groupBy, string? sumField, bool toModel)
    {
        var requested = groupBy.Concat(sumField is null ? [] : new[] { sumField }).ToArray();
        var entry = FindAuthorized(caller, reference, "result.aggregate", requested, toModel);
        EnsureSnapshot(entry, snapshot, schema);
        if (groupBy.Count > 4 || groupBy.Distinct(StringComparer.Ordinal).Count() != groupBy.Count) throw Invalid();
        _ = SelectFields(entry, requested.Length == 0 ? null : requested);
        return entry;
    }

    /// <summary>Release row payload while retaining bounded owner/replay metadata until expiry.</summary>
    public ResultRelease Release(ResultPrincipal caller, string reference, bool toModel = false)
    {
        lock (_gate)
        {
            var entry = FindAuthorized(caller, reference, "result.release", [], toModel, allowReleased: true);
            if (entry.Owner.SubjectNid != caller.SubjectNid) throw Unavailable();
            if (!entry.Released)
            {
                _retainedBytes -= entry.Payload.Length;
                entry.Payload = [];
                entry.Released = true;
            }
            return new ResultRelease(reference, true);
        }
    }

    /// <summary>
    /// Resolve a server-local, owner-only delegation scope under current read policy.
    /// A recipient read grant cannot create this input or recursively grant access.
    /// The returned scope is not an ActionFrame operation or a bearer credential.
    /// </summary>
    public ResultGrantScope GetOwnerGrantScope(ResultPrincipal caller, string reference)
    {
        lock (_gate)
        {
            var entry = FindAuthorized(caller, reference, "result.read", null, false);
            if (entry.Owner.SubjectNid != caller.SubjectNid) throw Unavailable();
            return new ResultGrantScope(entry.Descriptor, entry.Owner, entry.SourceId, entry.Fields);
        }
    }

    private Entry FindAuthorized(ResultPrincipal caller, string reference, string action,
        IReadOnlyList<string>? fields, bool toModel, bool allowReleased = false)
    {
        ValidatePrincipal(caller);
        RemoveExpired();
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512) throw Unavailable();
        if (!_entries.TryGetValue(reference, out var entry)
            || entry.Owner.Tenant != caller.Tenant || entry.Released && !allowReleased)
            throw Unavailable();
        Authorize(caller, action, entry.SourceId, entry.Owner.SubjectNid,
            fields ?? entry.Fields, toModel, entry);
        return entry;
    }

    private void Authorize(ResultPrincipal caller, string action, string source, string owner,
        IReadOnlyList<string> fields, bool toModel, Entry? entry = null)
    {
        try
        {
            if (!_policy(caller, new ResultAccess(action, source, owner, fields, toModel,
                    entry?.Descriptor.ResultRef, entry?.Descriptor.SnapshotId,
                    entry?.Owner.PolicyVersion ?? caller.PolicyVersion)))
                throw Unavailable();
        }
        catch (ActionNodeException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new ActionNodeException(503, NpsStatusCodes.ServerUnavailable,
            "NWP-RESULT-POLICY-UNAVAILABLE", "Result policy unavailable."); }
    }

    private bool TryReplay(ResultPrincipal caller,
        (string Tenant, string Subject, string Key) key,
        string digest, out ResultDescriptor? descriptor, bool toModel = false)
    {
        descriptor = null;
        if (!_keys.TryGetValue(key, out var reference)) return false;
        var entry = _entries[reference];
        Authorize(caller, "result.create", entry.SourceId, entry.Owner.SubjectNid, entry.Fields, toModel, entry);
        if (entry.RequestDigest != digest) throw ReplayConflict();
        if (entry.Released) throw Unavailable();
        descriptor = entry.Descriptor;
        return true;
    }

    private long Now()
    {
        _latestWallTime = Math.Max(_latestWallTime, _time.GetUtcNow().ToUnixTimeSeconds());
        return _latestWallTime;
    }

    private void RemoveExpired()
    {
        var now = Now();
        foreach (var entry in _entries.Values.Where(entry => now >= entry.Descriptor.ExpiresAt
                     || _time.GetElapsedTime(entry.Timestamp).TotalSeconds >= entry.Ttl).ToArray())
        {
            _entries.Remove(entry.Descriptor.ResultRef);
            _keys.Remove((entry.Owner.Tenant, entry.Owner.SubjectNid, entry.Key));
            _retainedBytes -= entry.Payload.Length + entry.MetadataBytes;
        }
    }

    private string WriteCursor(Entry entry, ResultPrincipal caller, string[] fields, int offset)
    {
        var value = new Cursor(entry.Descriptor.ResultRef, entry.Descriptor.SnapshotId,
            entry.Descriptor.SchemaRef, caller.Tenant, caller.SubjectNid, caller.PolicyVersion,
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields))), offset);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return Encode(bytes) + "." + Encode(HMACSHA256.HashData(_cursorKey, bytes));
    }

    private int ReadCursor(string cursor, Entry entry, ResultPrincipal caller, string[] fields)
    {
        try
        {
            if (cursor.Length > 4096) throw Conflict();
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw Conflict();
            var bytes = Decode(parts[0]);
            var signature = Decode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_cursorKey, bytes)))
                throw Conflict();
            var parsed = JsonSerializer.Deserialize<Cursor>(bytes);
            var expected = new Cursor(entry.Descriptor.ResultRef, entry.Descriptor.SnapshotId,
                entry.Descriptor.SchemaRef, caller.Tenant, caller.SubjectNid, caller.PolicyVersion,
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields))),
                parsed?.Offset ?? -1);
            if (parsed is null || parsed != expected || parsed.Offset < 0) throw Conflict();
            return parsed.Offset;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        { throw Conflict(); }
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
    }

    private static string[] SelectFields(Entry entry, IReadOnlyList<string>? fields)
    {
        if (fields is null) return entry.Fields;
        if (fields.Count == 0 || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count
            || fields.Any(field => !entry.Fields.Contains(field, StringComparer.Ordinal))) throw Invalid();
        return fields.ToArray();
    }

    private static void EnsureSnapshot(Entry entry, string snapshot, string schema)
    {
        if (entry.Descriptor.SnapshotId != snapshot || entry.Descriptor.SchemaRef != schema)
            throw Conflict();
    }

    private static void ValidatePrincipal(ResultPrincipal caller)
    {
        if (caller is null || new[] { caller.Tenant, caller.SubjectNid, caller.PolicyVersion }
            .Any(value => string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 512))
            throw new ActionNodeException(401, NpsStatusCodes.AuthUnauthenticated,
                "NWP-RESULT-IDENTITY-REQUIRED", "Verified result identity required.");
    }

    private static bool ValidDigest(string digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && digest.Length == 71 && digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static ActionNodeException Invalid() =>
        new(422, NpsStatusCodes.ClientUnprocessable, "NWP-ACTION-PARAMS-INVALID", "Invalid result parameters.");
    private static ActionNodeException Unavailable() =>
        new(404, NpsStatusCodes.ClientNotFound, "NWP-RESULT-NOT-FOUND", "Result unavailable.");
    private static ActionNodeException Conflict() =>
        new(409, NpsStatusCodes.ClientConflict, "NWP-RESULT-SNAPSHOT-MISMATCH", "Result binding mismatch.");
    private static ActionNodeException Capacity() =>
        new(429, NpsStatusCodes.LimitResource, "NWP-RESULT-CAPACITY", "Result capacity unavailable.");
    private static ActionNodeException ReplayConflict() =>
        new(409, NpsStatusCodes.ClientConflict, "NWP-RESULT-REPLAY-CONFLICT", "Result operation key conflict.");
}
