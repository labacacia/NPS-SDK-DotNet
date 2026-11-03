// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP.Discovery;

/// <summary>
/// Host-resolved CURRENT identity and ownership policy, independent of an announcement.
/// The resolver must authenticate the NID/key and authorize this cluster, epoch, anchor
/// role and exact HTTPS endpoint; an announcement cannot supply this authority.
/// </summary>
/// <param name="Nid">Authenticated publisher NID.</param>
/// <param name="PublicKey">Current ed25519: base64url verification key.</param>
/// <param name="Cluster">Authorized cluster identifier.</param>
/// <param name="Epoch">Authorized ownership epoch (positive).</param>
/// <param name="Endpoint">Host-approved HTTPS base URI, including its service path.</param>
/// <param name="TlsPin">Host-approved SHA-256 server certificate fingerprint.</param>
/// <param name="PolicyVersion">Host ownership/identity policy revision.</param>
/// <param name="ExpiresAt">Current identity/ownership expiry.</param>
public sealed record NdpRouteAuthority(string Nid, string PublicKey, string Cluster, ulong Epoch,
    Uri Endpoint, string TlsPin, string PolicyVersion, DateTimeOffset ExpiresAt);

/// <summary>Immutable, process-local route observation. It grants no result access or feature support.</summary>
public sealed class NdpRouteSelection
{
    internal NdpRouteSelection(string registryId, Guid generation, Guid admission, NdpRouteAuthority authority,
        ulong graphSequence, DateTimeOffset expiresAt, IEnumerable<string> capabilities)
    {
        RegistryId = registryId; Generation = generation; Admission = admission; Authority = authority;
        GraphSequence = graphSequence; ExpiresAt = expiresAt;
        Capabilities = Array.AsReadOnly(capabilities.ToArray());
    }

    internal Guid Generation { get; }
    internal Guid Admission { get; }
    internal NdpRouteAuthority Authority { get; }
    /// <summary>Host registry identity, for local evidence.</summary>
    public string RegistryId { get; }
    /// <summary>Issuer NID selected under current host policy.</summary>
    public string Nid => Authority.Nid;
    /// <summary>Exact host-approved endpoint. Changing it requires fresh admission.</summary>
    public Uri Endpoint => Authority.Endpoint;
    /// <summary>Selected owner epoch.</summary>
    public ulong Epoch => Authority.Epoch;
    /// <summary>Signed announcement sequence.</summary>
    public ulong GraphSequence { get; }
    /// <summary>Conservative signed/receipt/identity expiry; monotonic expiry is checked separately.</summary>
    public DateTimeOffset ExpiresAt { get; }
    /// <summary>Current host-approved certificate pin, to configure the actual transport.</summary>
    public string TlsPin => Authority.TlsPin;
    /// <summary>Coarse hints only. Authenticated NWM and operation admission remain mandatory.</summary>
    public IReadOnlyList<string> Capabilities { get; }
}

/// <summary>Fail-closed local discovery error; no dispatch has occurred.</summary>
public sealed class NdpDiscoveryException : Exception
{
    /// <summary>Creates a local error using an existing NDP code.</summary>
    public NdpDiscoveryException(string code) : base(code) => Code = code;
    /// <summary>Existing NDP error code.</summary>
    public string Code { get; }
}
