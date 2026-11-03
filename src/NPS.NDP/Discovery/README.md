English | [中文](README.cn.md)

# Opt-in issuer-pinned result discovery

`NdpResultDiscovery` is an explicitly selected, volatile .NET reference component.
It does not replace `InMemoryNdpRegistry`, `NdpRegistryProfile` or the registry daemon,
and does not offer `nps.production_runtime`. No wire fields or default offers change.
[Decision and limits](../../../../../docs/adr/2026-11-03-ndp-volatile-result-routing.md).

The host resolves a CURRENT `NdpRouteAuthority` from its trusted NIP/ownership policy,
independently of announcement claims. That policy authorizes the publisher key, cluster,
positive epoch, anchor role, exact HTTPS endpoint (including path), SHA-256 certificate
pin, revision and expiry. An unavailable resolver denies. Do not build this authority
from announcement fields, a caller-supplied boolean or model output.

`Admit(ReadOnlySpan<byte>)` accepts at most 64 KiB JSON/depth 16, rejects duplicate names,
verifies only portable canonical Ed25519 signatures, and retains up to 256 publisher
sequence/digest and cluster epoch tombstones. TTL is 0–900 seconds; zero marks offline.
Capacity exhaustion rejects new publishers rather than evicting replay protection.
A publisher cannot change clusters within one registry instance.

Signed timestamp+TTL, receipt+TTL, current identity expiry and monotonic receipt time
bound the lease. Unsigned `last_seen` may only shorten; unsigned draining cannot be
cleared at the same signed sequence. Retransmission cannot renew a lease. Higher signed
sequence is required to change the record. Lowest-owner fallback is forbidden, including
when the highest record is offline, expired, draining, unauthorized or lacks hints.
Recorded equal-epoch conflicts require a higher authorized epoch to recover, even after
one conflicting record expires. This conservative local rule is stricter than live-only
split handling. `GraphSeqRollback` also reports rejected lower ownership epochs.

`Resolve(cluster, requiredCapabilities)` returns an immutable local selection or throws
`NdpDiscoveryException.Code` using existing NDP codes. `Capabilities` are coarse hints;
no feature revision or resource permission follows from them. `ValidateCurrent(selection,
requiredCapabilities)` rechecks current authority, expiry, highest owner and the exact
admission/generation before each use. Save that selection alongside retained results.
Any endpoint, NID, epoch, sequence, certificate pin, key or host policy change invalidates
it. Construct a fresh `ResultHttpClient` using the selected endpoint/NID and actual TLS
pin; the client discovers authenticated NWM on every operation and enforces required
feature revisions. The receiver still checks CURRENT NIP/resource policy.

This is not an atomic network lease. A peer may change after validation: current receiver
admission and conservative uncertain-outcome handling remain essential. An old binding
cannot be silently rebound to a successor. Report `StateUnavailable`/stale route or the
result's `NWP-RESULT-NOT-FOUND`; never invent replicated state or automatically issue a
new create/replay. Keep uncertain original UUID/query/TTL for explicit owner recovery.
A cold registry restart creates a new generation and cannot restore remembered fences;
current ownership policy must be provisioned independently again. No durable recovery,
federation, shared result migration, native transport or other SDK parity is claimed.

[Runnable static and two-node topology](../../../../../docs/samples/ndp-result-routing.md).
