// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Core;
using NPS.NWP.ActionNode;

namespace NPS.NWP.Results;

/// <summary>
/// Deployment-owned live source authority. The host must derive this from current
/// owner NIP/resource policy and revoke/update it when that authority changes.
/// Changing PolicyVersion invalidates retained snapshots conservatively; rows are
/// never silently re-filtered under a new policy. No request can set these values.
/// </summary>
public sealed record ResultSourceAuthority(string Tenant, string OwnerSubject, string SourceId,
    string PolicyVersion, IReadOnlyList<string> Fields, IReadOnlyList<string> Operations,
    bool Enabled = true, bool AllowDelegation = false, bool AllowModelEgress = false);

/// <summary>A server-local grant management receipt; its ID cannot authorize a result operation.</summary>
public sealed record ResultRecipientGrant(string GrantId, long ExpiresAt);

/// <summary>
/// Bounded process-local resource policy for data results. It intersects recipient grants
/// with current source/owner authority, exact result/snapshot, operations, fields and egress.
/// It does not verify NIP/channel identity or replace live owner revocation integration.
/// </summary>
public sealed class ResultResourcePolicy
{
    private sealed record Source(string Version, HashSet<string> Fields, HashSet<string> Operations,
        bool Enabled, bool Delegation, bool ModelEgress, int Bytes);
    private sealed record Grant(ResultGrantScope Resource, string Recipient, HashSet<string> Operations,
        HashSet<string> Fields, bool ModelEgress, long ExpiresAt, long Timestamp, long Lifetime, int Bytes);
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Owner, string SourceId), Source> _sources = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly string _issuer;
    private readonly int _maxSources, _maxGrants;
    private readonly int _maxPolicyBytes;
    private readonly TimeProvider _time;
    private long _latestWall;
    private long _retainedBytes;

    /// <summary>
    /// Create a fail-closed policy for exactly one serving issuer. All sources, grants and
    /// egress start denied. Limits can lower but never exceed the finite reference ceilings.
    /// Store and policy must share a compatible clock; no state survives process restart.
    /// </summary>
    public ResultResourcePolicy(string issuerNid, int maxSources = 128, int maxGrants = 4096,
        TimeProvider? timeProvider = null, int maxPolicyBytes = 16 * 1024 * 1024)
    {
        if (!ValidText(issuerNid, 512) || maxSources is < 1 or > 128 || maxGrants is < 1 or > 4096
            || maxPolicyBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentException("Invalid result policy configuration.");
        _issuer = issuerNid;
        _maxSources = maxSources;
        _maxGrants = maxGrants;
        _maxPolicyBytes = maxPolicyBytes;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Install/update trusted source authority, copying all collections. This is host policy
    /// administration, not a remote grant API. Disabled authority immediately denies existing
    /// owner/recipient access; a new revision requires explicit recapture under the new policy.
    /// </summary>
    public void SetSourceAuthority(ResultSourceAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!ValidText(authority.Tenant, 512) || !ValidText(authority.OwnerSubject, 512)
            || !ValidText(authority.SourceId, 128) || !ValidText(authority.PolicyVersion, 512))
            throw new ArgumentException("Invalid source authority.", nameof(authority));
        var fields = CopyFields(authority.Fields);
        var operations = CopyOperations(authority.Operations, recipient: false);
        var key = (authority.Tenant, authority.OwnerSubject, authority.SourceId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(authority).Length + 1024;
        lock (_gate)
        {
            RemoveExpired();
            _sources.TryGetValue(key, out var existing);
            if (existing is null && _sources.Count >= _maxSources
                || _retainedBytes - (existing?.Bytes ?? 0) + bytes > _maxPolicyBytes) throw Capacity();
            _sources[key] = new(authority.PolicyVersion, fields, operations,
                authority.Enabled, authority.AllowDelegation, authority.AllowModelEgress, bytes);
            _retainedBytes += bytes - (existing?.Bytes ?? 0);
        }
    }

    /// <summary>
    /// Live store-policy callback. The supplied principal must already be authenticated by
    /// the host; caller-authored identities, model assertions and TrustFrames are insufficient.
    /// Unknown sources, stale snapshots, copied handles and cross-tenant grants all deny.
    /// </summary>
    public bool Authorize(ResultPrincipal caller, ResultAccess access)
    {
        lock (_gate)
        {
            RemoveExpired();
            if (!_sources.TryGetValue((caller.Tenant, access.OwnerSubject, access.SourceId), out var source)
                || !source.Enabled || source.Version != caller.PolicyVersion
                || access.CapturedPolicyVersion != source.Version
                || !source.Operations.Contains(access.ActionId)
                || access.Fields.Any(field => !source.Fields.Contains(field))
                || access.ToModel && !source.ModelEgress) return false;
            if (caller.SubjectNid == access.OwnerSubject) return true;
            if (!source.Delegation || access.ActionId is not ("result.read" or "result.aggregate")
                || access.ResultRef is null || access.SnapshotId is null) return false;
            return _grants.Values.Any(grant => grant.Resource.Owner.Tenant == caller.Tenant
                && grant.Resource.Owner.SubjectNid == access.OwnerSubject
                && grant.Resource.SourceId == access.SourceId
                && grant.Resource.Descriptor.IssuerNid == _issuer
                && grant.Resource.Descriptor.ResultRef == access.ResultRef
                && grant.Resource.Descriptor.SnapshotId == access.SnapshotId
                && grant.Resource.Owner.PolicyVersion == source.Version
                && grant.Recipient == caller.SubjectNid && grant.Operations.Contains(access.ActionId)
                && access.Fields.All(grant.Fields.Contains) && (!access.ToModel || grant.ModelEgress));
        }
    }

    /// <summary>
    /// Issue a recipient-bound grant from an owner-resolved store scope after explicit host
    /// delegation policy. Operations are read/aggregate only; fields/expiry/egress cannot exceed
    /// source authority or the captured result. Recipients cannot recursively grant or release.
    /// This method must never be directly exposed as an unauthenticated HTTP action.
    /// </summary>
    public ResultRecipientGrant IssueGrant(ResultPrincipal owner, ResultGrantScope resource,
        string recipientSubject, IReadOnlyList<string> operations, IReadOnlyList<string> fields,
        long expiresAt, bool allowModelEgress = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resource);
        if (!ValidText(recipientSubject, 512) || recipientSubject == owner.SubjectNid) throw Denied();
        var allowedFields = CopyFields(fields);
        var allowedOperations = CopyOperations(operations, recipient: true);
        lock (_gate)
        {
            RemoveExpired();
            var now = Now();
            if (resource.Descriptor.IssuerNid != _issuer || resource.Owner.Tenant != owner.Tenant
                || resource.Owner.SubjectNid != owner.SubjectNid
                || resource.Owner.PolicyVersion != owner.PolicyVersion
                || !_sources.TryGetValue((owner.Tenant, owner.SubjectNid, resource.SourceId), out var source)
                || !source.Enabled || !source.Delegation || source.Version != owner.PolicyVersion
                || !allowedFields.IsSubsetOf(source.Fields) || !allowedFields.IsSubsetOf(resource.Fields)
                || !allowedOperations.IsSubsetOf(source.Operations)
                || allowModelEgress && !source.ModelEgress
                || expiresAt <= now || expiresAt > resource.Descriptor.ExpiresAt
                || expiresAt - now > 900) throw Denied();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                { resource, recipientSubject, operations, fields, expiresAt, allowModelEgress }).Length + 1024;
            if (_grants.Count >= _maxGrants || _retainedBytes + bytes > _maxPolicyBytes) throw Capacity();
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _grants.Add(id, new(resource, recipientSubject, allowedOperations, allowedFields,
                allowModelEgress, expiresAt, _time.GetTimestamp(), expiresAt - now, bytes));
            _retainedBytes += bytes;
            return new(id, expiresAt);
        }
    }

    /// <summary>
    /// Owner-only server-local revocation. Unknown/other-tenant/other-owner IDs return false
    /// without disclosing a grant. Revocation cannot broaden policy or restore source access.
    /// </summary>
    public bool RevokeGrant(ResultPrincipal owner, string grantId)
    {
        lock (_gate)
        {
            RemoveExpired();
            if (!_grants.TryGetValue(grantId, out var grant)
                || grant.Resource.Owner.Tenant != owner.Tenant
                || grant.Resource.Owner.SubjectNid != owner.SubjectNid) return false;
            _retainedBytes -= grant.Bytes;
            return _grants.Remove(grantId);
        }
    }

    private void RemoveExpired()
    {
        var now = Now();
        foreach (var pair in _grants.Where(pair => now >= pair.Value.ExpiresAt
                     || _time.GetElapsedTime(pair.Value.Timestamp).TotalSeconds >= pair.Value.Lifetime).ToArray())
        {
            _grants.Remove(pair.Key);
            _retainedBytes -= pair.Value.Bytes;
        }
    }

    private long Now()
    {
        _latestWall = Math.Max(_latestWall, _time.GetUtcNow().ToUnixTimeSeconds());
        return _latestWall;
    }

    private static HashSet<string> CopyFields(IReadOnlyList<string> fields)
    {
        if (fields is null || fields.Count > 256 || fields.Any(field => !ValidText(field, 128))
            || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count)
            throw new ArgumentException("Invalid field policy.", nameof(fields));
        return new(fields, StringComparer.Ordinal);
    }

    private static HashSet<string> CopyOperations(IReadOnlyList<string> operations, bool recipient)
    {
        string[] supported = recipient ? ["result.read", "result.aggregate"]
            : ["result.create", "result.read", "result.aggregate", "result.release"];
        if (operations is null || operations.Count is < 1 or > 4
            || operations.Any(operation => !supported.Contains(operation, StringComparer.Ordinal))
            || operations.Distinct(StringComparer.Ordinal).Count() != operations.Count)
            throw new ArgumentException("Unsupported operation policy.", nameof(operations));
        return new(operations, StringComparer.Ordinal);
    }

    private static bool ValidText(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= maximum;
    private static ActionNodeException Denied() => new(404, NpsStatusCodes.ClientNotFound,
        "NWP-RESULT-NOT-FOUND", "Result unavailable.");
    private static ActionNodeException Capacity() => new(429, NpsStatusCodes.LimitResource,
        "NWP-RESULT-CAPACITY", "Result policy capacity unavailable.");
}
