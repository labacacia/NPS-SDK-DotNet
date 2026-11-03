English | [中文版](./CHANGELOG.cn.md)

# Changelog — .NET SDK

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Until NPS reaches v1.0 stable, every repository in the suite is synchronized to the same pre-release version tag.

---

## [1.0.0-alpha.20] — Release candidate, unpublished

- Bounded experimental result resources/authorization, repeated-schema batches, local task handoff and volatile node transfer. Language support differs; full six-SDK runtime parity is not qualified.
- Preserve measured comparisons with equivalent HTTPgzip and ordinary tools; no universal token, fee or speed benefit is claimed.
- Align candidate versions and local CI/package verification. Full production, beta and actual publication remain separate gates.

## [Unreleased]

- Update MessagePack to 3.1.9 and SQLite native bundles to 3.0.5; retain fixed wire vectors and source/publish-overlay alignment.

- Add explicitly selected volatile issuer-pinned result discovery with current host ownership policy, conservative freshness/owner fences and generation/endpoint binding. This does not qualify durable registry or full production runtime.
- Report locally invalid result descriptors as known no-dispatch; invalid received descriptors remain uncertain.

## [1.0.0-alpha.19] — 2026-09-05

### Added

- Added the public `NPS.Core.Alpha19` policy surface and executable coverage for all 47 shared NCP/NWP/NIP/NDP/NOP hardening vectors, including negative and non-fixture boundary paths.

### Changed

- Expanded Node L2 to v0.7/38 cases with current AaaS L2-01..L2-07; L2-01..05
  are required and L2-06/07 `na` results require a reasoned message.
- Aligned Node L2 manifests to v0.6 and all 31 cases; validation now enforces
  summary integrity, whole-family applicability and the single-/multi-Anchor
  HA invariant.
- Registered the alpha.19 protocol errors; the existing NativeAOT smoke remains green for the candidate.

## [1.0.0-alpha.18] — 2026-08-15

### Added

- Added official stateful LLM context DTOs, a process-local context store, and an Action Server coordinator with owner scoping, CAS reservations, lifecycle actions, true asynchronous execution, cancellation, and all 19 shared conformance vectors.
- Added a NativeAOT publish-and-run smoke host for generated frame codecs, including nullable `UInt64` round trips.

### Changed

- Aligned unary request correlation, LLM usage accounting, strict stateful request validation, and task ownership with the other five SDK families.

## [1.0.0-alpha.17] — 2026-08-02

### Changed

- Core, NWP, NIP, and NDP frame registries now use source-generated JSON and MessagePack metadata
  in their built-in registration paths.
- Tier-3 BinaryVector metadata is encoded with bounded MessagePack and JSON value-tree writers
  instead of runtime object serialization.
- Added `AddNcpHandshake()` and a NativeAOT-safe `AddNwp()` registry extension.
- Implement the shared NCP 0.11, NWP 0.20, NIP 0.13, NDP 0.12, and NOP 0.9
  portable profiles and language-neutral conformance fixtures.

### Fixed

- Tier-2 now preserves `JsonElement` content instead of serializing it as an empty map.
- Ivy NativeAOT publication no longer reports NPS-owned dynamic JSON or MessagePack resolver
  diagnostics; the runtime-generated registration overload remains available as an explicit
  compatibility fallback.
- SQLite storage now pins `SQLitePCLRaw.bundle_e_sqlite3 2.1.12` and no longer suppresses
  `GHSA-2m69-gcr7-jv3q`.

## [1.0.0-alpha.16] — 2026-07-23

### Changed

- Re-issued the alpha.15 .NET package family as alpha.16 so the suite could retain one
  immutable cross-repository version after the alpha.15 package coordinates were published.

## [1.0.0-alpha.15] — 2026-06-28

### Changed

- Suite-wide alpha.15 sync: aligned package metadata, current README/version banners, distribution source trees, and release-prep notes with NPS-Dev.
- Carries the NCP Tier-3 BinaryVector, inbound NWP Bridge server hardening, NIP canonical trust/revoke, and NDP discovery canonical-form alignment delivered by the source-of-truth tree.

## [1.0.0-alpha.14] — 2026-06-26

### Added

- `NPS.NWP.Bridge` now includes concrete outbound dispatchers for HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC.
- Added `BridgeNode`, `BridgeDispatcherRegistry`, `IBridgeDispatcher`, `BridgeNodeMiddleware`, `AddBridgeNode`, and `UseBridgeNode` for host-independent and ASP.NET-hosted Bridge dispatch.
- Added `BridgeEndpointValidator` with private/loopback SSRF guard and URI-aware `allowed_prefixes` validation.
- Added `HealthProbeRenderer` for transport-neutral daemon health/readiness output.
- Added `TrustFrameValidator` for open-source pinned-grantor TrustFrame validation.
- Added `NpsFrameCodec.CreateDefault()`, `NpsFrameCodec.Create(...)`, and instance `Peek(...)`.
- Added `NcpServerOptions` with a pre-preamble authenticated stream hook, handshake read timeout, and bounded Hello payload size.
- Added live revocation hooks for `NipIdentVerifier`: `NipRevocationCheck`, `NipVerifierOptions.RevocationStore`, and secure-by-default OCSP transport failure handling.
- Added `INipCaStore.ListAsync()` and a production package `InMemoryNipCaStore` for tests, demos, and local development stacks.
- Added `NipCaClient`, a typed remote CA client for discovery, CRL, Ed25519 registration/renewal/revocation/verification, and RFC-0002 X.509 registration.
- Added `NwpNativeNodeServer` so Memory and Action Nodes can serve native-mode NWP traffic over `NcpSession` or any NCP stream.
- Added `LabAcacia.NPS.Conformance` with Node L1/L2 case catalogs, run manifests, and validation helpers for CI self-certification.

### Changed

- Public package projects now emit SourceLink-enabled `.snupkg` symbol packages.
- NuGet publish workflow now packs and verifies the complete public .NET package family, including `LabAcacia.NPS.Conformance`.
- `ActionNodeMiddleware` and `ComplexNodeMiddleware` avoid nullable `CapsFrame.AnchorRef` assignments.
- NIP CA CRL responses now include `issued_at` and a detached CA signature; CA discovery points OCSP clients at mapped verify endpoints.
- Test and build output is warning-clean for the SDK test gate.

### Docs

- Added Bridge target schema docs and expanded .NET quickstarts for Core codec, DI, TrustFrame validation, observability, storage, native NWP serving, Bridge, ingress, remote CA, and conformance packages.

## [1.0.0-alpha.2] — 2026-04-19

### Changed

- `NPS.Core` README explicitly enumerates the full NCP frame set (AnchorFrame / DiffFrame / StreamFrame / CapsFrame / HelloFrame / ErrorFrame).
- `NPS.NWP` README calls out all four node types (Memory / Action / Complex / Gateway).
- Status: 495 tests green, including new wire-size benchmark, Gateway Node middleware, and A2A Bridge.

### Covered modules

- NPS.Core / NPS.NWP / NPS.NIP / NPS.NDP / NPS.NOP

---

## [1.0.0-alpha.1] — 2026-04-10

First public alpha as part of the NPS suite `v1.0.0-alpha.1` release.

[1.0.0-alpha.2]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.1
