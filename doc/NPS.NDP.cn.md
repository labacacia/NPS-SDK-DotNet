[English Version](./NPS.NDP.md) | 中文版

# `LabAcacia.NPS.NDP` — 类与方法参考

> 根命名空间：`NPS.NDP`
> NuGet：`LabAcacia.NPS.NDP`
> 规范：[NPS-4 NDP v0.8](https://github.com/labacacia/NPS/blob/main/NPS-4-NDP.md)

NDP 是发现层 —— NPS 对标 DNS。本包提供三种 NDP 帧类型、线程安全内存注册表、
announce 签名校验器,以及 DI 助手。

依赖：`LabAcacia.NPS.Core`、`LabAcacia.NPS.NIP`（`NdpAnnounceValidator` 使用 `NipSigner`）。

---

## 目录

- [NDP 帧](#ndp-帧)
  - [`NdpAddress` / `NdpResolveResult` / `NdpGraphNode`](#支撑类型)
  - [`AnnounceFrame`](#announceframe)
  - [`ResolveFrame`](#resolveframe)
  - [`GraphFrame`](#graphframe)
- [注册表](#注册表)
  - [`INdpRegistry`](#indpregistry)
  - [`InMemoryNdpRegistry`](#inmemoryndpregistry)
- [Announce 校验](#announce-校验)
  - [`NdpAnnounceValidator`](#ndpannouncevalidator)
  - [`NdpAnnounceResult`](#ndpannounceresult)
- [`NdpErrorCodes`](#ndperrorcodes)
- [DI + 注册表扩展](#di--注册表扩展)

---

## NDP 帧

### 支撑类型

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
    public          string?                  NodeType     { get; init; }   // "memory"、"action"、…
    public required IReadOnlyList<NdpAddress> Addresses   { get; init; }
    public required IReadOnlyList<string>    Capabilities { get; init; }
}
```

### `AnnounceFrame`

帧类型 `0x30` —— 发布节点的物理可达性与 TTL。

```csharp
public sealed record AnnounceFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Announce;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    public required string                   Nid          { get; init; }
    public          string?                  NodeType     { get; init; }
    public required IReadOnlyList<NdpAddress> Addresses   { get; init; }
    public required IReadOnlyList<string>    Capabilities { get; init; }
    public required uint                     Ttl          { get; init; }   // 0 = 有序关停
    public required string                   Timestamp    { get; init; }   // ISO 8601 UTC
    public required string                   Signature    { get; init; }   // ed25519:{base64url}
}
```

签名语义（NPS-4 §3.1）：

- 规范化帧时**排除** `signature` 字段。
- 用 NID 自己的私钥（同一把背后支持相应 `IdentFrame` 的密钥）签名。
- `Ttl = 0` 必须在优雅关停之前被签名并发布,使订阅者能够驱逐。

### `ResolveFrame`

帧类型 `0x31` —— 解析 `nwp://` URL 的请求 / 响应信封。因 resolve 流量小且常被
人工调试,JSON 为优先 tier。

```csharp
public sealed record ResolveFrame : IFrame
{
    public required string            Target       { get; init; }   // "nwp://api.example.com/products"
    public          string?           RequesterNid { get; init; }
    public          NdpResolveResult? Resolved     { get; init; }   // 响应时设置
}
```

### `GraphFrame`

帧类型 `0x32` —— 注册表间的拓扑同步。

```csharp
public sealed record GraphFrame : IFrame
{
    public required bool                       InitialSync { get; init; }
    public          IReadOnlyList<NdpGraphNode>? Nodes     { get; init; }  // InitialSync = true 时为完整快照
    public          JsonElement?               Patch       { get; init; }  // 否则为 RFC 6902 JSON Patch
    public required ulong                      Seq         { get; init; }  // 按发布者严格单调
}
```

`Seq` 出现跳变**必须**触发以 `NDP-GRAPH-SEQ-GAP` 信号的重新同步请求。

---

## 注册表

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

线程安全、TTL 驱逐、无后台 timer（过期在每次读取时惰性评估）。

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

行为：

- **`Announce`** —— `Ttl = 0` 立即驱逐条目；否则以绝对过期时间
  `Clock() + Ttl` 插入（或刷新）条目。
- **`Resolve`** —— 扫描当前活跃条目,找到第一个 NID 覆盖 `nwp://` target 的条目,
  返回第一个已广告地址。扫描过程中顺手清理不匹配 / 已过期条目。
- **`GetByNid`** —— 精确 NID 查找,带按需清理。
- **`Clock`** —— 可注入,便于确定性单元测试。

#### `NwpTargetMatchesNid(nid, target)`

实现 NID ↔ target 覆盖规则：

```
NID:    urn:nps:node:{authority}:{name}
Target: nwp://{authority}/{name}[/subpath]
```

节点 NID 覆盖 target 的条件：

1. 协议为 `nwp://`。
2. NID authority 与 target authority 相等（大小写不敏感）。
3. target 路径以 `/{name}` 起头,并在此结束或继续以 `/…` 延伸。

输入畸形时返回 `false` 而非抛出。

---

## Announce 校验

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

按 `NPS-4 §7.1`,校验器：

1. 在 `KnownPublicKeys` 中查找 NID。缺失 → `NDP-ANNOUNCE-NID-MISMATCH`
   （调用者应先通过 `NipIdentVerifier` 验证发布者的 `IdentFrame`,
   然后注册其 `pub_key`）。
2. 经 `NipSigner.DecodePublicKey` 解码已存储的密钥。
3. 经 `NipSigner.Verify` 校验 `AnnounceFrame` 签名。
4. 任何失败时返回 `NdpAnnounceResult.Fail(…)`；成功时返回 `NdpAnnounceResult.Ok()`。

编码后的密钥必须使用 `NipSigner.EncodePublicKey` 返回的
`ed25519:{base64url}` 形式。

### `NdpAnnounceResult`

```csharp
public sealed record NdpAnnounceResult
{
    public bool    IsValid   { get; init; }
    public string? ErrorCode { get; init; }   // 见 NdpErrorCodes
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

## DI + 注册表扩展

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

- `AddNdp` 将 `INdpRegistry → InMemoryNdpRegistry` 与 `NdpAnnounceValidator`
  注册为单例。通过在调用 `AddNdp` **之前**注册 `INdpRegistry` 来替换实现
  （如 Redis 支持的注册表）。
- 对 `FrameRegistryBuilder` 调用 `AddNdp` 将 `AnnounceFrame`、`ResolveFrame`、
  `GraphFrame` 注册进编解码器,使其可在线路往返。

---

## 端到端示例

```csharp
// 构建共享注册表
var registry  = new FrameRegistryBuilder().AddNcp().AddNip().AddNdp().Build();
var codec     = new NpsFrameCodec(registry);

// 配置 discovery
var services  = new ServiceCollection()
    .AddSingleton(codec)
    .AddNdp()
    .BuildServiceProvider();

var discovery = services.GetRequiredService<INdpRegistry>();
var validator = services.GetRequiredService<NdpAnnounceValidator>();

// 发布节点生成 Ed25519 身份并签署公告
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

// 注册发布者的公钥,然后校验 + 接受
validator.RegisterPublicKey(nid, NipSigner.EncodePublicKey(identity.PublicKey));
var v = validator.Validate(frame);
if (!v.IsValid) throw new InvalidOperationException(v.Message);

discovery.Announce(frame);

// 之后,消费者解析
var resolved = discovery.Resolve("nwp://api.example.com/products/items/42");
// → NdpResolveResult { Host = "10.0.0.5", Port = 17433, Ttl = 300 }
```
