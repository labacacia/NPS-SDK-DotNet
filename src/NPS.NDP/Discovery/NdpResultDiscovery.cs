// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.NDP.Discovery;

/// <summary>
/// Explicitly selected, bounded VOLATILE reference route admission for issuer-pinned
/// results. Not the durable registry daemon, a federation service or a dispatch authority.
/// Sequence/epoch tombstones are never evicted. Restart creates a new generation and
/// requires independently current host ownership policy; no historical recovery is claimed.
/// </summary>
public sealed class NdpResultDiscovery
{
    private sealed record Entry(AnnounceFrame Frame, NdpRouteAuthority Authority, string Digest,
        DateTimeOffset ExpiresAt, long ReceivedAt, TimeSpan Lifetime, bool Draining, Guid Admission);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _epochFloors = new(StringComparer.Ordinal);
    private readonly Guid _generation = Guid.NewGuid();
    private readonly string _registryId;
    private readonly Func<string, NdpRouteAuthority?> _resolveAuthority;
    private readonly TimeProvider _clock;
    private readonly int _capacity;

    /// <summary>
    /// Creates an empty reference registry. The synchronous host resolver is invoked on
    /// admission AND every resolution/validation, must return independently current policy,
    /// fail closed when unavailable, and must not call back into this registry.
    /// </summary>
    public NdpResultDiscovery(string registryId, Func<string, NdpRouteAuthority?> resolveAuthority,
        TimeProvider? clock = null, int capacity = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryId);
        ArgumentNullException.ThrowIfNull(resolveAuthority);
        if (capacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        _registryId = registryId; _resolveAuthority = resolveAuthority;
        _clock = clock ?? TimeProvider.System; _capacity = capacity;
    }

    /// <summary>
    /// Admits at most 64 KiB of JSON using ONLY the portable canonical Ed25519 signature.
    /// Signature/identity/ownership failures cannot advance fences. Unsigned health and
    /// last_seen may deny/shorten, never refresh expiry or clear a same-sequence drain veto.
    /// </summary>
    public NdpRegistryAdmission Admit(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > 65536) return Reject(NdpErrorCodes.AnnounceProfileViolation);
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RejectDuplicateNames(root);
            var frame = root.Deserialize<AnnounceFrame>();
            if (frame is null || !ValidShape(frame)) return Reject(NdpErrorCodes.AnnounceProfileViolation);
            // Duplicate JSON member names are rejected by canonicalization before any mutation.
            var digest = NdpAnnounceCanonicalizer.Digest(root);
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                var authority = CurrentAuthority(frame.Nid, now);
                if (authority is null || !Matches(frame, authority)) return Reject(NdpErrorCodes.IssuerNotAllowed);
                if (!NdpAnnounceCanonicalizer.Verify(root, authority.PublicKey, frame.Signature!))
                    return Reject(NdpErrorCodes.AnnounceSignatureInvalid);
                if (!DateTimeOffset.TryParse(frame.Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var signedAt) || signedAt > now.AddMinutes(5))
                    return Reject(NdpErrorCodes.AnnounceProfileViolation);
                var expires = Min(signedAt.AddSeconds(frame.Ttl), now.AddSeconds(frame.Ttl), authority.ExpiresAt);
                if (frame.LastSeen is not null)
                {
                    if (!DateTimeOffset.TryParse(frame.LastSeen, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var lastSeen)) return Reject(NdpErrorCodes.AnnounceProfileViolation);
                    expires = Min(expires, lastSeen.AddSeconds(frame.Ttl));
                }
                if (frame.Ttl != 0 && expires <= now) return Reject(NdpErrorCodes.AnnounceStale);
                _entries.TryGetValue(frame.Nid, out var previous);
                if (previous is not null)
                {
                    if (previous.Authority.Cluster != authority.Cluster) return Reject(NdpErrorCodes.AnnounceConflict);
                    if (frame.GraphSeq < previous.Frame.GraphSeq) return Reject(NdpErrorCodes.GraphSeqRollback);
                    if (frame.GraphSeq == previous.Frame.GraphSeq)
                    {
                        if (digest != previous.Digest) return Reject(NdpErrorCodes.AnnounceConflict);
                        var shortened = Min(previous.ExpiresAt, expires);
                        var drain = previous.Draining || frame.Health == "draining";
                        // A duplicate cannot revive the original monotonic or wall-clock lease.
                        _entries[frame.Nid] = previous with { ExpiresAt = shortened, Draining = drain,
                            Admission = shortened != previous.ExpiresAt || drain != previous.Draining ? Guid.NewGuid() : previous.Admission };
                        return new(NdpRegistryDecision.Duplicate);
                    }
                }
                if (_epochFloors.TryGetValue(authority.Cluster, out var floor) && authority.Epoch < floor)
                    return Reject(NdpErrorCodes.GraphSeqRollback);
                if (previous is null && _entries.Count >= _capacity) return Reject(NdpErrorCodes.StateUnavailable);
                _epochFloors[authority.Cluster] = authority.Epoch;
                _entries[frame.Nid] = new(frame, authority, digest, expires, _clock.GetTimestamp(),
                    TimeSpan.FromSeconds(frame.Ttl), frame.Health == "draining" || frame.Ttl == 0, Guid.NewGuid());
                return new(frame.Ttl == 0 ? NdpRegistryDecision.Removed : NdpRegistryDecision.Accepted);
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        { return Reject(NdpErrorCodes.AnnounceProfileViolation); }
    }

    /// <summary>
    /// Selects the unique highest recorded owner, failing rather than falling back if it
    /// expires, drains, loses current authority or lacks a required coarse capability.
    /// The caller must still negotiate required revisions using authenticated NWM/NIP.
    /// </summary>
    public NdpRouteSelection Resolve(string cluster, params string[] requiredCapabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        lock (_gate) return ResolveUnderLock(cluster, requiredCapabilities);
    }

    /// <summary>
    /// Revalidates a saved route immediately before each operation, including retained
    /// result access. A peer, endpoint, policy, sequence or generation change rejects the
    /// old binding; it never recreates a result or replays a mutation on a successor.
    /// This observation is not an atomic lease across network dispatch: receiver admission
    /// remains necessary and dispatch uncertainty must never trigger automatic replay.
    /// </summary>
    public void ValidateCurrent(NdpRouteSelection selection, params string[] requiredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        lock (_gate)
        {
            if (selection.Generation != _generation || selection.RegistryId != _registryId)
                throw new NdpDiscoveryException(NdpErrorCodes.StateUnavailable);
            var current = ResolveUnderLock(selection.Authority.Cluster, requiredCapabilities);
            if (current.Admission != selection.Admission || current.Authority != selection.Authority)
                throw new NdpDiscoveryException(NdpErrorCodes.StateUnavailable);
        }
    }

    private NdpRouteSelection ResolveUnderLock(string cluster, string[] capabilities)
    {
        if (!_epochFloors.TryGetValue(cluster, out var floor)) throw new NdpDiscoveryException(NdpErrorCodes.ResolveNotFound);
        // A recorded equal-epoch ownership conflict requires a higher, host-authorized
        // epoch to recover; expiry alone cannot authorize choosing one conflicting owner.
        var candidates = _entries.Values.Where(entry => entry.Authority.Cluster == cluster && entry.Authority.Epoch == floor).ToArray();
        if (candidates.Length != 1) throw new NdpDiscoveryException(NdpErrorCodes.ClusterSplit);
        var entry = candidates[0];
        var now = _clock.GetUtcNow();
        if (entry.Draining || entry.ExpiresAt <= now || _clock.GetElapsedTime(entry.ReceivedAt) >= entry.Lifetime)
            throw new NdpDiscoveryException(NdpErrorCodes.ResolveStale);
        if (CurrentAuthority(entry.Frame.Nid, now) != entry.Authority)
            throw new NdpDiscoveryException(NdpErrorCodes.IssuerNotAllowed);
        if (capabilities.Any(capability => !entry.Frame.Capabilities.Contains(capability, StringComparer.Ordinal)))
            throw new NdpDiscoveryException(NdpErrorCodes.ResolveNotFound);
        return new(_registryId, _generation, entry.Admission, entry.Authority, entry.Frame.GraphSeq,
            entry.ExpiresAt, entry.Frame.Capabilities);
    }

    private NdpRouteAuthority? CurrentAuthority(string nid, DateTimeOffset now)
    {
        NdpRouteAuthority? value;
        try { value = _resolveAuthority(nid); }
        catch { return null; } // Unavailable host authority cannot become permissive discovery.
        if (value is null || value.Nid != nid || value.Epoch == 0 || value.ExpiresAt <= now
            || string.IsNullOrWhiteSpace(value.Cluster) || string.IsNullOrWhiteSpace(value.PolicyVersion)
            || value.PublicKey is null || !value.PublicKey.StartsWith("ed25519:", StringComparison.Ordinal)
            || value.Endpoint is null || !value.Endpoint.IsAbsoluteUri || value.Endpoint.Scheme != "https"
            || value.Endpoint.UserInfo.Length != 0 || value.Endpoint.Query.Length != 0 || value.Endpoint.Fragment.Length != 0
            || value.TlsPin is null || value.TlsPin.Length != 64 || !value.TlsPin.All(Uri.IsHexDigit)) return null;
        return value;
    }

    private static void RejectDuplicateNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON member.");
                RejectDuplicateNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateNames(item);
    }

    private static bool Matches(AnnounceFrame frame, NdpRouteAuthority authority) =>
        frame.ClusterAnchor == authority.Cluster && frame.ClusterEpoch == authority.Epoch
        && frame.NodeRoles!.Contains("anchor", StringComparer.Ordinal)
        && frame.Addresses.Any(address => address.Protocol == "https" && address.Host == authority.Endpoint.Host
            && address.Port == authority.Endpoint.Port);

    private static bool ValidShape(AnnounceFrame frame) => frame.Frame == "0x30"
        && !string.IsNullOrWhiteSpace(frame.Nid) && frame.GraphSeq > 0 && frame.ClusterEpoch > 0
        && frame.NodeType == "action" && frame.Ttl <= 900 && frame.Signature is not null
        && frame.Addresses is { Count: > 0 and <= 8 } && frame.Addresses.All(address => address is not null)
        && frame.Capabilities is { Count: <= 32 } && frame.Capabilities.All(cap => !string.IsNullOrWhiteSpace(cap))
        && frame.NodeRoles is { Count: > 0 and <= 8 }
        && frame.Health is null or "healthy" or "degraded" or "draining";

    private static DateTimeOffset Min(params DateTimeOffset[] values) => values.Min();
    private static NdpRegistryAdmission Reject(string code) => new(NdpRegistryDecision.Rejected, code);
}
