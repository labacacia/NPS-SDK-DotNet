English | [中文版](./NPS.NIP.cn.md)

# `LabAcacia.NPS.NIP` — Class and Method Reference

> Root namespace: `NPS.NIP`
> NuGet: `LabAcacia.NPS.NIP`
> Spec: [NPS-3 NIP v0.9](https://github.com/labacacia/NPS/blob/main/NPS-3-NIP.md)

NIP is the identity / PKI layer. The package covers:

1. Ed25519 key material and signing (`NipKeyManager`, `NipSigner`).
2. A fully embeddable Certificate Authority service (`NipCaService` + `NipCaRouter`).
3. The IdentFrame / TrustFrame / RevokeFrame types.
4. A six-step node-side verifier (`NipIdentVerifier`) implementing `NPS-3 §7`.
5. DI + pipeline extensions for CA servers and verifying nodes.

---

## Table of contents

- [Key management](#key-management)
  - [`NipKeyManager`](#nipkeymanager)
  - [`NipSigner`](#nipsigner)
- [NIP frames](#nip-frames)
  - [`IdentFrame` + `IdentMetadata`](#identframe--identmetadata)
  - [`TrustFrame`](#trustframe)
  - [`RevokeFrame`](#revokeframe)
- [Certificate Authority](#certificate-authority)
  - [`NipCaOptions`](#nipcaoptions)
  - [`NipCertRecord` + `INipCaStore`](#nipcertrecord--inipcastore)
  - [`NipCaService`](#nipcaservice)
  - [`NipCaException` + `NipErrorCodes`](#nipcaexception--niperrorcodes)
  - [`NipCaRouter`](#nipcarouter)
  - [`PostgreSqlNipCaStore`](#postgresqlnipcastore)
- [Node-side verification](#node-side-verification)
  - [`NipVerifierOptions`](#nipverifieroptions)
  - [`NipVerifyContext`](#nipverifycontext)
  - [`NipIdentVerifyResult` / `NipVerifyResult`](#nipidentverifyresult--nipverifyresult)
  - [`NipIdentVerifier`](#nipidentverifier)
- [DI extensions](#di-extensions)

---

## Key management

### `NipKeyManager`

```csharp
namespace NPS.NIP.Crypto;

public sealed class NipKeyManager
{
    public void   Generate(string path, string passphrase);
    public void   Load    (string path, string passphrase);
    public bool   IsLoaded { get; }
    public byte[] PublicKey  { get; }    // 32 bytes
    public byte[] PrivateKey { get; }    // 32 bytes (Ed25519 seed)
}
```

Persists the private key encrypted on disk with the following layout:

```
[ 12 bytes nonce ][ 16 bytes GCM tag ][ encrypted 32-byte private key ][ plaintext 32-byte public key ]
```

- Cipher: **AES-256-GCM**
- KDF: **PBKDF2-HMAC-SHA256**, **600 000 iterations** (OWASP 2023 recommended minimum)
- Salt is the first 16 bytes of the nonce buffer

`Generate` writes a freshly rolled keypair atomically; `Load` fails fast if the GCM tag doesn't
match (tampering or wrong passphrase). Both the public and private key are kept in memory for the
lifetime of the manager; call `new NipKeyManager()` per process.

### `NipSigner`

```csharp
public static class NipSigner
{
    public static string Sign<TFrame>(byte[] privateKey, TFrame frame);
    public static bool   Verify<TFrame>(byte[] publicKey, TFrame frame, string signature);

    public static string  EncodePublicKey(byte[] key);     // "ed25519:{base64url}"
    public static byte[]? DecodePublicKey(string encoded); // null on malformed input
}
```

Signing rules:

1. Serialise the frame as **canonical JSON** — keys sorted lexically, no whitespace — after
   excluding the `signature` and `metadata` fields.
2. Ed25519-sign the UTF-8 canonical bytes.
3. Return the signature as `ed25519:{base64url(sig)}`.

`Verify` recomputes the canonical bytes and returns `false` on any failure (unknown prefix,
malformed base64, wrong length, or cryptographic mismatch) — it does not throw.

---

## NIP frames

### `IdentFrame` + `IdentMetadata`

```csharp
public sealed record IdentFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Ident;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    public required string        Nid           { get; init; }   // "urn:nps:agent:..."
    public required string        EntityType    { get; init; }   // "agent" | "node" | "operator"
    public required string        PubKey        { get; init; }   // "ed25519:{base64url}"
    public required string        Serial        { get; init; }
    public required string        IssuedBy      { get; init; }   // CA NID
    public required DateTime      IssuedAt      { get; init; }
    public required DateTime      ExpiresAt     { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required JsonElement   Scope         { get; init; }   // e.g. {"nwp": ["api.example.com/*"]}
    public required string        Signature     { get; init; }   // CA signature
    public          IdentMetadata? Metadata     { get; init; }   // client-declared, not signed
}

public sealed record IdentMetadata
{
    public string?                 Operator    { get; init; }
    public string?                 Model       { get; init; }
    public string?                 Version     { get; init; }
    public IReadOnlyList<string>?  Tags        { get; init; }
    public JsonElement?            Extensions  { get; init; }
}
```

`Metadata` is deliberately excluded from the signature context so agents can update human-readable
tags without re-issuing the certificate.

### `TrustFrame`

```csharp
public sealed record TrustFrame : IFrame
{
    public required string        Issuer     { get; init; }   // the delegator
    public required string        Subject    { get; init; }   // the delegate NID
    public required JsonElement   Scope      { get; init; }   // subset of issuer scope
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required DateTime      NotBefore  { get; init; }
    public required DateTime      NotAfter   { get; init; }
    public required string        Signature  { get; init; }
}
```

Used for A→A delegation within a session. The verifier validates `Scope ⊆ Issuer.Scope`.

### `RevokeFrame`

```csharp
public sealed record RevokeFrame : IFrame
{
    public required string   Nid       { get; init; }
    public required string   Serial    { get; init; }
    public required string   Reason    { get; init; }   // "keyCompromise"|"superseded"|...
    public required DateTime RevokedAt { get; init; }
    public required string   IssuedBy  { get; init; }   // CA NID
    public required string   Signature { get; init; }   // CA signature
}
```

---

## Certificate Authority

### `NipCaOptions`

```csharp
public sealed class NipCaOptions
{
    public required string   CaNid           { get; set; }
    public          string?  DisplayName     { get; set; }
    public required string   KeyFilePath     { get; set; }
    public required string   KeyPassphrase   { get; set; }           // MUST come from env var
    public          int      AgentCertValidityDays { get; set; } = 30;
    public          int      NodeCertValidityDays  { get; set; } = 90;
    public          int      RenewalWindowDays     { get; set; } = 7;
    public required string   BaseUrl         { get; set; }
    public          string   RoutePrefix     { get; set; } = "";
    public required string   ConnectionString{ get; set; }
    public          bool     NormalizeOcspResponseTime { get; set; } = true;
    public          IReadOnlyList<string> Algorithms { get; set; } = ["ed25519"];
}
```

Timing-oracle guard: `NormalizeOcspResponseTime = true` pads every OCSP response to ≥ 200 ms to
prevent status inference from latency (`NPS-3 §10.2`).

### `NipCertRecord` + `INipCaStore`

```csharp
public sealed class NipCertRecord
{
    public required string   Nid          { get; init; }
    public required string   EntityType   { get; init; }
    public required string   Serial       { get; init; }
    public required string   PubKey       { get; init; }
    public required string[] Capabilities { get; init; }
    public required string   ScopeJson    { get; init; }   // JSON blob
    public required string   IssuedBy     { get; init; }
    public required DateTime IssuedAt     { get; init; }
    public required DateTime ExpiresAt    { get; init; }
    public          DateTime? RevokedAt   { get; init; }
    public          string?  RevokeReason { get; init; }
    public          string?  MetadataJson { get; init; }
}

public interface INipCaStore
{
    Task  SaveAsync        (NipCertRecord record, CancellationToken ct = default);
    Task<NipCertRecord?> GetByNidAsync   (string nid,    CancellationToken ct = default);
    Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default);
    Task<bool> RevokeAsync          (string nid, string reason, DateTime revokedAt, CancellationToken ct = default);
    Task<string> NextSerialAsync    (CancellationToken ct = default);
    Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default);
}
```

`NextSerialAsync` MUST be atomic (PostgreSQL sequences satisfy this; the in-memory test double
uses `Interlocked.Increment`).

### `NipCaService`

```csharp
public sealed class NipCaService
{
    public NipCaService(NipCaOptions opts, INipCaStore store, NipKeyManager keyManager);

    public Task<IdentFrame>        RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    public Task<IdentFrame>        RenewAsync   (string nid,                          CancellationToken ct = default);
    public Task<RevokeFrame>       RevokeAsync  (string nid, string reason,           CancellationToken ct = default);
    public Task<NipVerifyResult>   VerifyAsync  (string nid,                          CancellationToken ct = default);
    public Task<IReadOnlyList<RevokeFrame>> GetCrlAsync(CancellationToken ct = default);
    public string                  GetCaPublicKey();        // "ed25519:{base64url}"
    public static string           BuildNid(string entityType, string domain, string name);
}
```

Behavioural notes:

- **`RegisterAsync`** allocates a fresh serial, builds an `IdentFrame` with `IssuedBy = CaNid`,
  signs it with `NipKeyManager.PrivateKey`, persists the record, and returns the signed frame.
- **`RenewAsync`** rejects with `NIP-RENEWAL-TOO-EARLY` when `now < expires_at - RenewalWindowDays`.
- **`RevokeAsync`** records the revocation time and reason; subsequent `VerifyAsync`/OCSP return
  `CertRevoked`.
- **`VerifyAsync`** returns a `NipVerifyResult` suitable for OCSP responses (`active` /
  `revoked` / `unknown`).
- **`GetCrlAsync`** returns all revoked certificates as a signed CRL (list of `RevokeFrame`).
- **`BuildNid`** is a helper: `BuildNid("agent", "example.com", "planner")` →
  `urn:nps:agent:example.com:planner`.

### `NipCaException` + `NipErrorCodes`

```csharp
public sealed class NipCaException : Exception
{
    public string Code { get; }
    public NipCaException(string code, string message);
}

public static class NipErrorCodes
{
    public const string CertExpired        = "NIP-CERT-EXPIRED";
    public const string CertRevoked        = "NIP-CERT-REVOKED";
    public const string CertSigInvalid     = "NIP-CERT-SIG-INVALID";
    public const string CertUntrusted      = "NIP-CERT-UNTRUSTED";
    public const string CertCapMissing     = "NIP-CERT-CAP-MISSING";
    public const string CertScope          = "NIP-CERT-SCOPE";
    public const string NidNotFound        = "NIP-NID-NOT-FOUND";
    public const string NidAlreadyExists   = "NIP-NID-ALREADY-EXISTS";
    public const string RenewalTooEarly    = "NIP-RENEWAL-TOO-EARLY";
    public const string OcspUnavailable    = "NIP-OCSP-UNAVAILABLE";
}
```

### `NipCaRouter`

```csharp
namespace NPS.NIP.Http;

public static class NipCaRouter
{
    public static void MapNipCa(IEndpointRouteBuilder app, NipCaOptions opts, NipCaService ca);
}
```

Mounts the following routes (rooted at `opts.RoutePrefix`):

| Route                                | Verb  | Purpose                          |
|--------------------------------------|-------|----------------------------------|
| `/.well-known/nps-ca`                | GET   | CA metadata, public key          |
| `/ca/register`                       | POST  | Issue a new certificate          |
| `/ca/renew`                          | POST  | Renew an existing cert           |
| `/ca/revoke`                         | POST  | Revoke a cert                    |
| `/ca/ocsp`                           | POST  | OCSP status check                |
| `/ca/crl`                            | GET   | Current CRL (list of RevokeFrame)|
| `/ca/idents/{nid}`                   | GET   | Fetch a specific IdentFrame      |

### `PostgreSqlNipCaStore`

Production-grade `INipCaStore` implementation. Table layout is created idempotently on first
connection:

```
nip_certificates (
    nid            text primary key,
    entity_type    text not null,
    serial         text not null unique,
    pub_key        text not null,
    capabilities   text[] not null,
    scope          jsonb not null,
    issued_by      text not null,
    issued_at      timestamptz not null,
    expires_at     timestamptz not null,
    revoked_at     timestamptz,
    revoke_reason  text,
    metadata       jsonb
);

nip_serial_seq      sequence
```

Constructor: `new PostgreSqlNipCaStore(connectionString)`.

---

## Node-side verification

### `NipVerifierOptions`

```csharp
public sealed class NipVerifierOptions
{
    // Map of CA NID → CA public key ("ed25519:{base64url}")
    public required IReadOnlyDictionary<string, string> TrustedIssuers { get; set; }

    public bool   EnableOcsp        { get; set; } = true;
    public string OcspEndpointPath  { get; set; } = "/ca/ocsp";
    public int    OcspTimeoutMs     { get; set; } = 500;
    public bool   OcspFailOpen      { get; set; } = true;   // RFC 6960 §2.4 default
    public bool   EnableCrlFallback { get; set; } = true;
}
```

`OcspFailOpen = true` means OCSP network failures do NOT reject the certificate — the verifier
falls back to local CRL and treats unknown-status responses as "not revoked". This matches the
default web-PKI behaviour.

### `NipVerifyContext`

```csharp
public sealed record NipVerifyContext(
    string? RequiredCapability,   // e.g. "nwp:query"
    string? RequiredNwpPath,      // e.g. "nwp://api.example.com/products"
    DateTime? Now = null);        // override clock for tests
```

### `NipIdentVerifyResult` / `NipVerifyResult`

```csharp
public sealed record NipIdentVerifyResult
{
    public bool    IsValid     { get; init; }
    public string? ErrorCode   { get; init; }
    public string? ErrorMessage{ get; init; }

    public static NipIdentVerifyResult Ok();
    public static NipIdentVerifyResult Fail(string code, string message);
}

public sealed record NipVerifyResult
{
    public string   Status    { get; init; }   // "active" | "revoked" | "unknown"
    public DateTime? RevokedAt { get; init; }
    public string?  Reason    { get; init; }
}
```

### `NipIdentVerifier`

```csharp
namespace NPS.NIP.Verification;

public sealed class NipIdentVerifier
{
    public NipIdentVerifier(
        NipVerifierOptions opts,
        IHttpClientFactory? httpFactory = null,
        ILogger<NipIdentVerifier>? log  = null);

    public Task<NipIdentVerifyResult> VerifyAsync(
        IdentFrame frame,
        NipVerifyContext ctx,
        CancellationToken ct = default);
}
```

Executes the **six-step flow from NPS-3 §7** in order:

1. **Expiry** — reject when `now ≥ ExpiresAt` (`CertExpired`).
2. **Trusted issuer** — reject when `frame.IssuedBy` is not in `NipVerifierOptions.TrustedIssuers`
   (`CertUntrusted`).
3. **Signature** — `NipSigner.Verify` against the trusted issuer's public key (`CertSigInvalid`).
4. **Revocation** — OCSP POST (honours `OcspFailOpen`), falling back to local CRL; reject when
   status is `revoked` (`CertRevoked`).
5. **Capability** — reject when `ctx.RequiredCapability` is not in `frame.Capabilities`
   (`CertCapMissing`).
6. **Scope** — reject when `ctx.RequiredNwpPath` is not covered by `frame.Scope["nwp"]`
   (`CertScope`). The helper `NwpPathMatches` understands:
   - `*` — match everything
   - `api.example.com/*` — prefix match (trailing `/*`)
   - exact matches otherwise

Any step returning `Fail` short-circuits — subsequent steps are skipped.

---

## DI extensions

```csharp
namespace NPS.NIP.Extensions;

public static class NipServiceExtensions
{
    public static IServiceCollection AddNipCa(
        this IServiceCollection services,
        Action<NipCaOptions> configure,
        bool generateKeyIfMissing = false);

    public static IServiceCollection AddNipVerifier(
        this IServiceCollection services,
        Action<NipVerifierOptions> configure);

    public static IEndpointRouteBuilder MapNipCa(this IEndpointRouteBuilder app);
    public static WebApplication       MapNipCa(this WebApplication app);
}
```

- `AddNipCa` wires `NipCaOptions`, loads (or generates) the keypair, constructs the
  `PostgreSqlNipCaStore`, and registers `NipCaService` as a singleton. Set
  `generateKeyIfMissing = true` only in dev.
- `AddNipVerifier` registers `NipIdentVerifier` together with its options. Supply an
  `IHttpClientFactory` via `services.AddHttpClient()` to enable connection pooling for OCSP.
- `MapNipCa` mounts the HTTP routes (`/.well-known/nps-ca`, `/ca/…`) on the router.

### Registry extension

Every `FrameRegistryBuilder` pipeline must pass through `AddNip()` to enable NCP decoding of NIP
frames:

```csharp
namespace NPS.NIP.Registry;

public static class NipRegistryExtensions
{
    public static FrameRegistryBuilder AddNip(this FrameRegistryBuilder b);
}
```

---

## End-to-end example — running a CA

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNpsCore();
builder.Services.AddHttpClient();

builder.Services.AddNipCa(o =>
{
    o.CaNid            = "urn:nps:org:ca.example.com";
    o.DisplayName      = "Example CA";
    o.KeyFilePath      = "/var/lib/nips-ca/ca.key";
    o.KeyPassphrase    = Environment.GetEnvironmentVariable("NIPS_CA_PASSPHRASE")
                            ?? throw new InvalidOperationException("NIPS_CA_PASSPHRASE not set");
    o.BaseUrl          = "https://ca.example.com";
    o.ConnectionString = builder.Configuration.GetConnectionString("NipCa")!;
});

var app = builder.Build();
app.UseRouting();
app.MapNipCa();
app.Run();
```

## End-to-end example — verifying an incoming agent

```csharp
builder.Services.AddHttpClient();
builder.Services.AddNipVerifier(o => o.TrustedIssuers = new Dictionary<string, string>
{
    ["urn:nps:org:ca.example.com"] = "ed25519:AAAA...",
});

// Somewhere in middleware:
var verifier = sp.GetRequiredService<NipIdentVerifier>();
var result = await verifier.VerifyAsync(identFrame,
    new NipVerifyContext(
        RequiredCapability: "nwp:query",
        RequiredNwpPath:    "nwp://api.example.com/products"));

if (!result.IsValid)
    return Results.Problem(result.ErrorMessage, statusCode: 401);
```
