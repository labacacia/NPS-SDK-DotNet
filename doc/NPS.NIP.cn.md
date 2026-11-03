[English Version](./NPS.NIP.md) | 中文版

# `LabAcacia.NPS.NIP` — 类与方法参考

> 根命名空间：`NPS.NIP`
> NuGet：`LabAcacia.NPS.NIP`
> 规范：[NPS-3 NIP v0.9](https://github.com/labacacia/NPS/blob/main/NPS-3-NIP.md)

NIP 是身份 / PKI 层。本包覆盖：

1. Ed25519 密钥材料与签名（`NipKeyManager`、`NipSigner`）。
2. 可完全嵌入的 CA 服务（`NipCaService` + `NipCaRouter`）。
3. IdentFrame / TrustFrame / RevokeFrame 类型。
4. 实现 `NPS-3 §7` 的六步节点侧验证器（`NipIdentVerifier`）。
5. 供 CA 服务端和验证节点使用的 DI + 管道扩展。

---

## 目录

- [密钥管理](#密钥管理)
  - [`NipKeyManager`](#nipkeymanager)
  - [`NipSigner`](#nipsigner)
- [NIP 帧](#nip-帧)
  - [`IdentFrame` + `IdentMetadata`](#identframe--identmetadata)
  - [`TrustFrame`](#trustframe)
  - [`RevokeFrame`](#revokeframe)
- [证书颁发机构](#证书颁发机构)
  - [`NipCaOptions`](#nipcaoptions)
  - [`NipCertRecord` + `INipCaStore`](#nipcertrecord--inipcastore)
  - [`NipCaService`](#nipcaservice)
  - [`NipCaException` + `NipErrorCodes`](#nipcaexception--niperrorcodes)
  - [`NipCaRouter`](#nipcarouter)
  - [`PostgreSqlNipCaStore`](#postgresqlnipcastore)
- [节点侧验证](#节点侧验证)
  - [`NipVerifierOptions`](#nipverifieroptions)
  - [`NipVerifyContext`](#nipverifycontext)
  - [`NipIdentVerifyResult` / `NipVerifyResult`](#nipidentverifyresult--nipverifyresult)
  - [`NipIdentVerifier`](#nipidentverifier)
- [DI 扩展](#di-扩展)

---

## 密钥管理

### `NipKeyManager`

```csharp
namespace NPS.NIP.Crypto;

public sealed class NipKeyManager
{
    public void   Generate(string path, string passphrase);
    public void   Load    (string path, string passphrase);
    public bool   IsLoaded { get; }
    public byte[] PublicKey  { get; }    // 32 字节
    public byte[] PrivateKey { get; }    // 32 字节（Ed25519 seed）
}
```

以下列布局将私钥加密持久化到磁盘：

```
[ 12 字节 nonce ][ 16 字节 GCM tag ][ 加密的 32 字节私钥 ][ 明文 32 字节公钥 ]
```

- 加密算法：**AES-256-GCM**
- KDF：**PBKDF2-HMAC-SHA256**,**600 000 次迭代**（OWASP 2023 推荐最小值）
- Salt 为 nonce 缓冲区的前 16 字节

`Generate` 原子地写入新生成的密钥对；`Load` 在 GCM tag 不匹配（篡改或口令错误）
时快速失败。公钥与私钥均在 manager 生命期内留在内存；每进程调用一次
`new NipKeyManager()`。

### `NipSigner`

```csharp
public static class NipSigner
{
    public static string Sign<TFrame>(byte[] privateKey, TFrame frame);
    public static bool   Verify<TFrame>(byte[] publicKey, TFrame frame, string signature);

    public static string  EncodePublicKey(byte[] key);     // "ed25519:{base64url}"
    public static byte[]? DecodePublicKey(string encoded); // 输入畸形时返回 null
}
```

签名规则：

1. 将帧序列化为**规范 JSON** —— 键按字典序,无空白 —— 并排除
   `signature` 与 `metadata` 字段。
2. 对 UTF-8 规范字节做 Ed25519 签名。
3. 以 `ed25519:{base64url(sig)}` 返回签名。

`Verify` 重新计算规范字节,遇任何失败（未知前缀、畸形 base64、长度错误、
或密码学不匹配）返回 `false` —— 它不抛异常。

---

## NIP 帧

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
    public required JsonElement   Scope         { get; init; }   // 如 {"nwp": ["api.example.com/*"]}
    public required string        Signature     { get; init; }   // CA 签名
    public          IdentMetadata? Metadata     { get; init; }   // 客户端声明,不参与签名
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

`Metadata` 被刻意排除在签名上下文之外,使得 agent 可在不重签证书的情况下
更新人类可读标签。

### `TrustFrame`

```csharp
public sealed record TrustFrame : IFrame
{
    public required string        Issuer     { get; init; }   // 委托方
    public required string        Subject    { get; init; }   // 被委托 NID
    public required JsonElement   Scope      { get; init; }   // 是 Issuer scope 的子集
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required DateTime      NotBefore  { get; init; }
    public required DateTime      NotAfter   { get; init; }
    public required string        Signature  { get; init; }
}
```

用于会话内 A→A 委托。校验器会校验 `Scope ⊆ Issuer.Scope`。

### `RevokeFrame`

```csharp
public sealed record RevokeFrame : IFrame
{
    public required string   Nid       { get; init; }
    public required string   Serial    { get; init; }
    public required string   Reason    { get; init; }   // "keyCompromise"|"superseded"|...
    public required DateTime RevokedAt { get; init; }
    public required string   IssuedBy  { get; init; }   // CA NID
    public required string   Signature { get; init; }   // CA 签名
}
```

---

## 证书颁发机构

### `NipCaOptions`

```csharp
public sealed class NipCaOptions
{
    public required string   CaNid           { get; set; }
    public          string?  DisplayName     { get; set; }
    public required string   KeyFilePath     { get; set; }
    public required string   KeyPassphrase   { get; set; }           // 必须来自环境变量
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

时序 oracle 防御：`NormalizeOcspResponseTime = true` 将每个 OCSP 响应
padding 到 ≥ 200 ms,防止通过延迟推断状态（`NPS-3 §10.2`）。

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

`NextSerialAsync` 必须原子（PostgreSQL 序列满足此要求；内存测试替身使用
`Interlocked.Increment`）。

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

行为要点：

- **`RegisterAsync`** 分配新 serial,构建 `IssuedBy = CaNid` 的 `IdentFrame`,
  使用 `NipKeyManager.PrivateKey` 签名,持久化记录,返回已签名的帧。
- **`RenewAsync`** 在 `now < expires_at - RenewalWindowDays` 时以
  `NIP-RENEWAL-TOO-EARLY` 拒绝。
- **`RevokeAsync`** 记录吊销时间与原因；后续 `VerifyAsync`/OCSP 返回 `CertRevoked`。
- **`VerifyAsync`** 返回适合 OCSP 响应的 `NipVerifyResult`
  （`active` / `revoked` / `unknown`）。
- **`GetCrlAsync`** 返回所有已吊销证书作为已签名的 CRL（`RevokeFrame` 列表）。
- **`BuildNid`** 是辅助方法：`BuildNid("agent", "example.com", "planner")` →
  `urn:nps:agent:example.com:planner`。

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

挂载以下路由（以 `opts.RoutePrefix` 为根）：

| 路由                                 | 动词  | 用途                                |
|--------------------------------------|-------|-------------------------------------|
| `/.well-known/nps-ca`                | GET   | CA 元数据、公钥                     |
| `/ca/register`                       | POST  | 签发新证书                          |
| `/ca/renew`                          | POST  | 续签已有证书                        |
| `/ca/revoke`                         | POST  | 吊销证书                            |
| `/ca/ocsp`                           | POST  | OCSP 状态检查                       |
| `/ca/crl`                            | GET   | 当前 CRL（`RevokeFrame` 列表）      |
| `/ca/idents/{nid}`                   | GET   | 取回指定 IdentFrame                 |

### `PostgreSqlNipCaStore`

生产级 `INipCaStore` 实现。表结构在首次连接时幂等创建：

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

构造：`new PostgreSqlNipCaStore(connectionString)`。

---

## 节点侧验证

### `NipVerifierOptions`

```csharp
public sealed class NipVerifierOptions
{
    // CA NID → CA 公钥（"ed25519:{base64url}"）的映射
    public required IReadOnlyDictionary<string, string> TrustedIssuers { get; set; }

    public bool   EnableOcsp        { get; set; } = true;
    public string OcspEndpointPath  { get; set; } = "/ca/ocsp";
    public int    OcspTimeoutMs     { get; set; } = 500;
    public bool   OcspFailOpen      { get; set; } = true;   // RFC 6960 §2.4 默认
    public bool   EnableCrlFallback { get; set; } = true;
}
```

`OcspFailOpen = true` 意味着 OCSP 网络故障**不**拒绝证书 —— 验证器回退
到本地 CRL,并将 unknown-status 响应视为"未吊销"。与默认的 web-PKI 行为一致。

### `NipVerifyContext`

```csharp
public sealed record NipVerifyContext(
    string? RequiredCapability,   // 如 "nwp:query"
    string? RequiredNwpPath,      // 如 "nwp://api.example.com/products"
    DateTime? Now = null);        // 测试中覆盖 clock
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

按顺序执行 **NPS-3 §7 的六步流程**：

1. **过期** —— 当 `now ≥ ExpiresAt` 时拒绝（`CertExpired`）。
2. **可信颁发者** —— 当 `frame.IssuedBy` 不在 `NipVerifierOptions.TrustedIssuers`
   中时拒绝（`CertUntrusted`）。
3. **签名** —— 使用可信颁发者公钥调用 `NipSigner.Verify`（`CertSigInvalid`）。
4. **吊销** —— OCSP POST（尊重 `OcspFailOpen`）,回退到本地 CRL；状态为
   `revoked` 时拒绝（`CertRevoked`）。
5. **能力** —— 当 `ctx.RequiredCapability` 不在 `frame.Capabilities` 中时拒绝
   （`CertCapMissing`）。
6. **Scope** —— 当 `ctx.RequiredNwpPath` 未被 `frame.Scope["nwp"]` 覆盖时拒绝
   （`CertScope`）。助手 `NwpPathMatches` 理解：
   - `*` —— 匹配一切
   - `api.example.com/*` —— 前缀匹配（末尾 `/*`）
   - 其他情况为精确匹配

任一步返回 `Fail` 即短路 —— 跳过后续步骤。

---

## DI 扩展

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

- `AddNipCa` 连接 `NipCaOptions`、加载（或生成）密钥对、构造
  `PostgreSqlNipCaStore`、并将 `NipCaService` 注册为单例。
  `generateKeyIfMissing = true` 仅在开发中使用。
- `AddNipVerifier` 将 `NipIdentVerifier` 连同其 options 一起注册。
  通过 `services.AddHttpClient()` 提供 `IHttpClientFactory` 以启用 OCSP
  连接池。
- `MapNipCa` 在路由器上挂载 HTTP 路由（`/.well-known/nps-ca`、`/ca/…`）。

### 注册表扩展

每个 `FrameRegistryBuilder` 管道必须经过 `AddNip()` 以启用 NIP 帧的 NCP 解码：

```csharp
namespace NPS.NIP.Registry;

public static class NipRegistryExtensions
{
    public static FrameRegistryBuilder AddNip(this FrameRegistryBuilder b);
}
```

---

## 端到端示例 —— 运行 CA

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

## 端到端示例 —— 验证入站 agent

```csharp
builder.Services.AddHttpClient();
builder.Services.AddNipVerifier(o => o.TrustedIssuers = new Dictionary<string, string>
{
    ["urn:nps:org:ca.example.com"] = "ed25519:AAAA...",
});

// 在 middleware 某处：
var verifier = sp.GetRequiredService<NipIdentVerifier>();
var result = await verifier.VerifyAsync(identFrame,
    new NipVerifyContext(
        RequiredCapability: "nwp:query",
        RequiredNwpPath:    "nwp://api.example.com/products"));

if (!result.IsValid)
    return Results.Problem(result.ErrorMessage, statusCode: 401);
```
