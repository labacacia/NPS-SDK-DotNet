// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NWP.Results;

/// <summary>
/// Identity supplied by a trusted host after authentication and scope validation.
/// Never deserialize this type from action parameters or construct it from X-NWP-Agent.
/// Resource policy and model egress are checked separately on every operation.
/// </summary>
/// <param name="Tenant">Trusted tenant/workspace binding.</param>
/// <param name="SubjectNid">Authenticated caller NID, not a routing hint.</param>
/// <param name="PolicyVersion">Current policy revision used to fence page cursors.</param>
public sealed record ResultPrincipal(string Tenant, string SubjectNid, string PolicyVersion);

/// <summary>
/// Input to the deployment-owned resource policy. These values describe server-owned
/// state, not a caller-authored grant. Return false when current source/grant policy denies.
/// </summary>
/// <param name="ActionId">Exact result operation being authorized.</param>
/// <param name="SourceId">Allowlisted source whose policy must still permit access.</param>
/// <param name="OwnerSubject">Principal that captured this snapshot.</param>
/// <param name="Fields">Fields that the operation would expose or aggregate.</param>
/// <param name="ToModel">Whether the operation crosses the external-model boundary.</param>
/// <param name="ResultRef">Exact server-owned result, absent before initial capture.</param>
/// <param name="SnapshotId">Captured snapshot, absent before initial capture.</param>
/// <param name="CapturedPolicyVersion">Owner policy revision at capture; live policy may invalidate it.</param>
public sealed record ResultAccess(string ActionId, string SourceId, string OwnerSubject,
    IReadOnlyList<string> Fields, bool ToModel, string? ResultRef = null,
    string? SnapshotId = null, string? CapturedPolicyVersion = null);

/// <summary>
/// Server-local delegation input obtained from an owner-authorized store lookup.
/// This is not a wire credential. Only the store constructs it; no caller-authored
/// descriptor can establish resource ownership or the captured source/field binding.
/// </summary>
public sealed class ResultGrantScope
{
    internal ResultGrantScope(ResultDescriptor descriptor, ResultPrincipal owner,
        string sourceId, IReadOnlyList<string> fields)
    {
        Descriptor = descriptor;
        Owner = owner;
        SourceId = sourceId;
        Fields = Array.AsReadOnly(fields.ToArray());
    }
    /// <summary>Exact immutable result descriptor resolved by the store.</summary>
    public ResultDescriptor Descriptor { get; }
    /// <summary>Original capture owner, not the current recipient of a read grant.</summary>
    public ResultPrincipal Owner { get; }
    /// <summary>Allowlisted source whose current authority constrains delegation.</summary>
    public string SourceId { get; }
    /// <summary>Fields captured into this result; a grant can only narrow them.</summary>
    public IReadOnlyList<string> Fields { get; }
}

/// <summary>
/// Immutable source capture supplied by the host's consistent-snapshot adapter.
/// Rows must be a JSON array of objects, already restricted to authorized source rows.
/// The store copies the bytes and never re-queries a live source while paging.
/// </summary>
/// <param name="SourceVersion">Version of the captured source state.</param>
/// <param name="SchemaRef">Existing SHA-256 schema anchor reference.</param>
/// <param name="Rows">Consistent finite source records; the caller may dispose its document after capture.</param>
public sealed record ResultSnapshot(string SourceVersion, string SchemaRef, JsonElement Rows);

/// <summary>Public, authorized metadata; it contains no default preview or authority-bearing token.</summary>
public sealed record ResultDescriptor(
    [property: JsonPropertyName("result_ref")] string ResultRef,
    [property: JsonPropertyName("issuer_nid")] string IssuerNid,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("schema_ref")] string SchemaRef,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("retention")] string Retention);

/// <summary>A bounded page tied to the descriptor's immutable snapshot and schema.</summary>
public sealed record ResultPage(
    [property: JsonPropertyName("descriptor")] ResultDescriptor Descriptor,
    [property: JsonPropertyName("rows")] IReadOnlyList<JsonElement> Rows,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>Bounded exact COUNT/SUM groups with the source snapshot as provenance.</summary>
public sealed record ResultAggregate(
    [property: JsonPropertyName("descriptor")] ResultDescriptor Descriptor,
    [property: JsonPropertyName("groups")] IReadOnlyList<JsonElement> Groups);

/// <summary>An owner-authorized release receipt; repeated release does not disclose row data.</summary>
public sealed record ResultRelease(
    [property: JsonPropertyName("result_ref")] string ResultRef,
    [property: JsonPropertyName("released")] bool Released);

/// <summary>
/// Process-local result limits. Values may lower the frozen reference maxima, never raise them.
/// Metadata/tombstones are separately bounded by MaxRecords; no durable retention is promised.
/// </summary>
public sealed record ResultStoreOptions
{
    /// <summary>Stable identity of the serving issuer; changing it does not migrate existing results.</summary>
    public required string IssuerNid { get; init; }
    /// <summary>Maximum simultaneously readable results for one tenant/principal.</summary>
    public int MaxResultsPerOwner { get; init; } = 16;
    /// <summary>Hard finite row count of one immutable source capture.</summary>
    public int MaxRowsPerResult { get; init; } = 100_000;
    /// <summary>Maximum serialized row payload retained for a single result.</summary>
    public int MaxResultBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Combined logical row/metadata/replay budget, enforced atomically at publication.</summary>
    public long MaxStoreBytes { get; init; } = 128 * 1024 * 1024;
    /// <summary>Bound on live plus released metadata/replay records retained until expiry.</summary>
    public int MaxRecords { get; init; } = 4096;
    /// <summary>Authority-preserving lifetime of a result and its release/replay metadata.</summary>
    public int DefaultTtlSeconds { get; init; } = 300;
    /// <summary>Hard TTL ceiling; rollback of wall time cannot extend remaining TTL.</summary>
    public int MaxTtlSeconds { get; init; } = 900;
}
