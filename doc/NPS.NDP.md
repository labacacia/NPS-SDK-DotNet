English | [中文版](./NPS.NDP.cn.md)

# `LabAcacia.NPS.NDP` — Class and Method Reference

> Root namespace: `NPS.NDP`
> NuGet: `LabAcacia.NPS.NDP`
> Spec: [NPS-4 NDP v0.8](https://github.com/labacacia/NPS/blob/main/NPS-4-NDP.md)

NDP is the discovery layer — the NPS analogue of DNS. This package provides the three NDP frame
types, a thread-safe in-memory registry, an announce signature validator, and DI helpers.

Dependencies: `LabAcacia.NPS.Core`, `LabAcacia.NPS.NIP` (`NdpAnnounceValidator` uses `NipSigner`).

---

## Table of contents

- [NDP frames](#ndp-frames)
  - [`NdpAddress` / `NdpResolveResult` / `NdpGraphNode`](#supporting-types)
  - [`AnnounceFrame`](#announceframe)
  - [`ResolveFrame`](#resolveframe)
  - [`GraphFrame`](#graphframe)
- [Registry](#registry)
  - [`INdpRegistry`](#indpregistry)
  - [`InMemoryNdpRegistry`](#inmemoryndpregistry)
- [Announce validation](#announce-validation)
  - [`NdpAnnounceValidator`](#ndpannouncevalidator)
  - [`NdpAnnounceResult`](#ndpannounceresult)
- [`NdpErrorCodes`](#ndperrorcodes)
- [DI + registry extensions](#di--registry-extensions)

---

## NDP frames

### Supporting types

```csharp
public sealed record NdpAddress
{
    public required string Host     { get; init; }
    public required int    Port     { get; init; }
    public required string Protocol { get; init; }   // "nwp" | "nwp+tls"
}

public sealed record NdpResolveResult
{
    public required string  Host            { get; init; }
    public required int     Port            { get; init; }
    public          string? CertFingerprint { get; init; }   // "sha256:{hex}"
    public required uint    Ttl             { get; init; }
}

public sealed record NdpGraphNode
{
    public required string                   Nid          { get; init; }
    public          string?                  NodeType     { get; init; }   // "memory","action",…
    public required IReadOnlyList<NdpAddress> Addresses   { get; init; }
    public required IReadOnlyList<string>    Capabilities { get; init; }
}
```

### `AnnounceFrame`

Frame type `0x30` — publishes a node's physical reachability and TTL.

```csharp
public sealed record AnnounceFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Announce;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    public required string                   Nid          { get; init; }
    public          string?                  NodeType     { get; init; }
    public required IReadOnlyList<NdpAddress> Addresses   { get; init; }
    public required IReadOnlyList<string>    Capabilities { get; init; }
    public required uint                     Ttl          { get; init; }   // 0 = orderly shutdown
    public required string                   Timestamp    { get; init; }   // ISO 8601 UTC
    public required string                   Signature    { get; init; }   // ed25519:{base64url}
}
```

Signature semantics (NPS-4 §3.1):

- Canonicalise the frame with the `signature` field **excluded**.
- Sign with the NID's own private key (the same key that backs the corresponding `IdentFrame`).
- `Ttl = 0` must be signed and published before graceful shutdown so subscribers can evict.

### `ResolveFrame`

Frame type `0x31` — request/response envelope for resolving a `nwp://` URL. JSON is the preferred
tier since resolve traffic is low-volume and human-debugged.

```csharp
public sealed record ResolveFrame : IFrame
{
    public required string            Target       { get; init; }   // "nwp://api.example.com/products"
    public          string?           RequesterNid { get; init; }
    public          NdpResolveResult? Resolved     { get; init; }   // set on response
}
```

### `GraphFrame`

Frame type `0x32` — topology sync between registries.

```csharp
public sealed record GraphFrame : IFrame
{
    public required bool                       InitialSync { get; init; }
    public          IReadOnlyList<NdpGraphNode>? Nodes     { get; init; }  // full snapshot when InitialSync = true
    public          JsonElement?               Patch       { get; init; }  // RFC 6902 JSON Patch otherwise
    public required ulong                      Seq         { get; init; }  // strictly monotonic per publisher
}
```

A gap in `Seq` MUST trigger a re-sync request signalled with `NDP-GRAPH-SEQ-GAP`.

---

## Registry

### `INdpRegistry`

```csharp
public interface INdpRegistry
{
    void                        Announce(AnnounceFrame frame);
    NdpResolveResult?           Resolve (string target);
    IReadOnlyList<AnnounceFrame> GetAll ();
    AnnounceFrame?              GetByNid(string nid);
}
```

### `InMemoryNdpRegistry`

Thread-safe, TTL-evicting, no background timer (expiry is evaluated lazily on every read).

```csharp
public sealed class InMemoryNdpRegistry : INdpRegistry
{
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public void                        Announce(AnnounceFrame frame);
    public NdpResolveResult?           Resolve (string target);
    public IReadOnlyList<AnnounceFrame> GetAll ();
    public AnnounceFrame?              GetByNid(string nid);

    public static bool NwpTargetMatchesNid(string nid, string target);
}
```

Behaviour:

- **`Announce`** — `Ttl = 0` immediately evicts the entry; otherwise the entry is inserted (or
  refreshed) with an absolute expiry = `Clock() + Ttl`.
- **`Resolve`** — scans currently-live entries for the first whose NID covers the `nwp://` target,
  returning the first advertised address. Non-matching/expired entries are purged during the scan.
- **`GetByNid`** — exact NID lookup with on-demand purge.
- **`Clock`** — injectable for deterministic unit tests.

#### `NwpTargetMatchesNid(nid, target)`

Implements the NID ↔ target covering rule:

```
NID:    urn:nps:node:{authority}:{name}
Target: nwp://{authority}/{name}[/subpath]
```

A node NID covers a target when:

1. The scheme is `nwp://`.
2. The NID authority equals the target authority (case-insensitive).
3. The target path starts with `/{name}` and either ends there or continues with `/…`.

Returns `false` for malformed inputs rather than throwing.

---

## Announce validation

### `NdpAnnounceValidator`

```csharp
public sealed class NdpAnnounceValidator
{
    public void RegisterPublicKey(string nid, string encodedPubKey);
    public void RemovePublicKey  (string nid);
    public IReadOnlyDictionary<string, string> KnownPublicKeys { get; }

    public NdpAnnounceResult Validate(AnnounceFrame frame);
}
```

Per `NPS-4 §7.1`, the validator:

1. Looks up the NID in `KnownPublicKeys`. Absence → `NDP-ANNOUNCE-NID-MISMATCH` (callers should
   first verify the announcer's `IdentFrame` via `NipIdentVerifier` and then register its
   `pub_key`).
2. Decodes the stored key via `NipSigner.DecodePublicKey`.
3. Verifies the `AnnounceFrame` signature via `NipSigner.Verify`.
4. On any failure returns `NdpAnnounceResult.Fail(…)`; on success returns `NdpAnnounceResult.Ok()`.

The encoded key MUST use the `ed25519:{base64url}` form returned by `NipSigner.EncodePublicKey`.

### `NdpAnnounceResult`

```csharp
public sealed record NdpAnnounceResult
{
    public bool    IsValid   { get; init; }
    public string? ErrorCode { get; init; }   // see NdpErrorCodes
    public string? Message   { get; init; }

    public static NdpAnnounceResult Ok();
    public static NdpAnnounceResult Fail(string errorCode, string message);
}
```

---

## `NdpErrorCodes`

```csharp
public static class NdpErrorCodes
{
    public const string ResolveNotFound          = "NDP-RESOLVE-NOT-FOUND";
    public const string ResolveAmbiguous         = "NDP-RESOLVE-AMBIGUOUS";
    public const string ResolveTimeout           = "NDP-RESOLVE-TIMEOUT";
    public const string AnnounceSignatureInvalid = "NDP-ANNOUNCE-SIGNATURE-INVALID";
    public const string AnnounceNidMismatch      = "NDP-ANNOUNCE-NID-MISMATCH";
    public const string GraphSeqGap              = "NDP-GRAPH-SEQ-GAP";
    public const string RegistryUnavailable      = "NDP-REGISTRY-UNAVAILABLE";
}
```

---

## DI + registry extensions

```csharp
namespace NPS.NDP.Extensions;

public static class NdpServiceExtensions
{
    public static IServiceCollection AddNdp(this IServiceCollection services);
}

namespace NPS.NDP.Registry;

public static class NdpRegistryExtensions
{
    public static FrameRegistryBuilder AddNdp(this FrameRegistryBuilder builder);
}
```

- `AddNdp` registers `INdpRegistry → InMemoryNdpRegistry` and `NdpAnnounceValidator` as singletons.
  Substitute the implementation (e.g. a Redis-backed registry) by registering `INdpRegistry`
  **before** calling `AddNdp`.
- `AddNdp` on the `FrameRegistryBuilder` registers `AnnounceFrame`, `ResolveFrame`, `GraphFrame`
  into the codec so they round-trip on the wire.

---

## End-to-end sample

```csharp
// Build the shared registry
var registry  = new FrameRegistryBuilder().AddNcp().AddNip().AddNdp().Build();
var codec     = new NpsFrameCodec(registry);

// Configure discovery
var services  = new ServiceCollection()
    .AddSingleton(codec)
    .AddNdp()
    .BuildServiceProvider();

var discovery = services.GetRequiredService<INdpRegistry>();
var validator = services.GetRequiredService<NdpAnnounceValidator>();

// A publisher node generates an Ed25519 identity and signs its announcement
var identity = new NipKeyManager();
identity.Generate("node.key", Environment.GetEnvironmentVariable("KEY_PASS")!);

var nid       = "urn:nps:node:api.example.com:products";
var unsigned  = new AnnounceFrame
{
    Nid          = nid,
    NodeType     = "memory",
    Addresses    = [new NdpAddress { Host = "10.0.0.5", Port = 17433, Protocol = "nwp+tls" }],
    Capabilities = ["nwp:query", "nwp:stream"],
    Ttl          = 300,
    Timestamp    = DateTime.UtcNow.ToString("O"),
    Signature    = "placeholder",
};
var sig   = NipSigner.Sign(identity.PrivateKey, unsigned);
var frame = unsigned with { Signature = sig };

// Register the announcer's pub key, then validate + accept
validator.RegisterPublicKey(nid, NipSigner.EncodePublicKey(identity.PublicKey));
var v = validator.Validate(frame);
if (!v.IsValid) throw new InvalidOperationException(v.Message);

discovery.Announce(frame);

// Later, a consumer resolves
var resolved = discovery.Resolve("nwp://api.example.com/products/items/42");
// → NdpResolveResult { Host = "10.0.0.5", Port = 17433, Ttl = 300 }
```
