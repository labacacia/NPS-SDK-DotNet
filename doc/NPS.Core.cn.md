[English Version](./NPS.Core.md) | 中文版

# `LabAcacia.NPS.Core` — 类与方法参考

> 根命名空间：`NPS.Core`
> NuGet：`LabAcacia.NPS.Core`
> 规范：[NPS-1 NCP v0.7](https://github.com/labacacia/NPS/blob/main/NPS-1-NCP.md)

本包持有线路格式、帧编解码器、帧类型注册表、anchor 缓存、以及所有共享异常 / 错误码。
SDK 中其他所有包都依赖本包。

---

## 目录

- [帧](#帧)
  - [`IFrame`](#iframe)
  - [`FrameType`](#frametype)
  - [`FrameFlags`](#frameflags)
  - [`FrameHeader`](#frameheader)
  - [`FrameSchema` / `SchemaField`](#frameschema--schemafield)
- [NCP 帧（`NPS.Core.Frames.Ncp`）](#ncp-帧)
  - [`AnchorFrame`](#anchorframe)
  - [`DiffFrame` + `JsonPatchOperation`](#diffframe)
  - [`StreamFrame`](#streamframe)
  - [`CapsFrame`](#capsframe)
  - [`ErrorFrame`](#errorframe)
  - [`HelloFrame`](#helloframe)
  - [`AlignFrame`（已弃用）](#alignframe-已弃用)
- [编解码器](#编解码器)
  - [`NpsFrameCodec`](#npsframecodec)
  - [`Tier1JsonCodec` / `Tier2MsgPackCodec`](#tier1jsoncodec--tier2msgpackcodec)
- [Anchor 缓存](#anchor-缓存)
  - [`AnchorIdComputer`](#anchoridcomputer)
  - [`AnchorFrameCache`](#anchorframecache)
- [帧注册表](#帧注册表)
  - [`FrameRegistry` / `FrameRegistryBuilder`](#frameregistry--frameregistrybuilder)
- [错误码、状态码与异常](#错误码状态码与异常)
- [Patch 格式常量](#patch-格式常量)
- [DI 扩展 `AddNpsCore`](#addnpscore)

---

## 帧

### `IFrame`

```csharp
namespace NPS.Core.Frames;

public interface IFrame
{
    FrameType    FrameType     { get; }
    EncodingTier PreferredTier { get; }
}
```

每个 NPS 帧（跨所有协议）都实现此接口。

| 成员            | 用途                                                                            |
|-----------------|---------------------------------------------------------------------------------|
| `FrameType`     | 写入帧头的字节判别符（参见 [`FrameType`](#frametype)）。                        |
| `PreferredTier` | 逐帧编码偏好。可被编解码器的 `overrideTier` 参数覆盖。                         |

### `FrameType`

```csharp
public enum FrameType : byte
{
    Hello       = 0x00,
    // NCP
    Anchor      = 0x01,
    Diff        = 0x02,
    Stream      = 0x03,
    Caps        = 0x04,
    Align       = 0x05,   // 已弃用
    // NWP
    Query       = 0x10,
    Action      = 0x11,
    // NIP
    Ident       = 0x20,
    Trust       = 0x21,
    Revoke      = 0x22,
    // NDP
    Announce    = 0x30,
    Resolve     = 0x31,
    Graph       = 0x32,
    // NOP
    Task        = 0x40,
    Delegate    = 0x41,
    Sync        = 0x42,
    AlignStream = 0x43,
    // Error
    Error       = 0xFE,
}
```

权威来源：`spec/frame-registry.yaml`。注册表命名空间按协议分为每 16 字节一个块 ——
NCP 的新帧类型从 `0x06+` 起，NOP 从 `0x44+` 起，依此类推。

### `FrameFlags`

```csharp
[Flags]
public enum FrameFlags : byte
{
    Tier1Json    = 0x00,   // 低位清零
    Tier2MsgPack = 0x01,   // 低位置位
    Final        = 0x04,   // 流终帧
    Encrypted    = 0x08,   // payload 为 NIP 加密
    Ext          = 0x80,   // 扩展帧头 —— 4 字节 payload 长度
}
```

Flag 字节是帧头第二字节。`Tier1Json` 与 `Tier2MsgPack` 互斥（低位）。
`Final`、`Encrypted` 与 `Ext` 为独立位。

### `FrameHeader`

```csharp
public readonly record struct FrameHeader(
    FrameType    FrameType,
    EncodingTier Tier,
    FrameFlags   Flags,
    uint         PayloadLength,
    bool         IsExtended);
```

#### 常量

| 常量                             | 值                         |
|----------------------------------|----------------------------|
| `FrameHeader.DefaultSize`        | `4`                        |
| `FrameHeader.ExtendedSize`       | `8`                        |
| `FrameHeader.DefaultMaxPayload`  | `ushort.MaxValue`（65 535）|
| `FrameHeader.ExtendedMaxPayload` | `uint.MaxValue`            |

#### `static FrameHeader Parse(ReadOnlySpan<byte> source, out int bytesRead)`

从 `source` 开头解析帧头。`bytesRead` 返回 4 或 8，取决于 EXT flag 是否置位。
以下情形抛出 `NpsCodecError`：

- `source.Length < 4`。
- EXT 置位但 `source.Length < 8`。

#### `int WriteTo(Span<byte> destination)`

将帧头序列化到 `destination`，返回写入字节数（4 或 8）。缓冲区过小时抛出
`ArgumentException`。

### `FrameSchema` / `SchemaField`

```csharp
public sealed record FrameSchema
{
    public IReadOnlyList<SchemaField> Fields { get; init; } = [];
    public string?                    Family { get; init; }   // 可选 schema 族标签
}

public sealed record SchemaField
{
    public required string Name     { get; init; }
    public required string Type     { get; init; }            // 如 "uint64"、"string"、"decimal"
    public string?         Semantic { get; init; }            // 如 "commerce.price.usd"
    public bool            Required { get; init; }
    public bool            PrimaryKey { get; init; }
    public bool            Nullable { get; init; }
    public JsonElement?    Default  { get; init; }
    public string?         Description { get; init; }
}
```

Schema 嵌入在 `AnchorFrame` 内部，由 `anchor_id` 引用。

---

## NCP 帧

所有 NCP 帧位于 `NPS.Core.Frames`（顶层 `namespace NPS.Core.Frames.Ncp`
仅为内部组织细节 —— 公开类型暴露在 `NPS.Core.Frames.*`）。

### `AnchorFrame`

```csharp
public sealed record AnchorFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Anchor;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    public required string      AnchorId  { get; init; }   // "sha256:<hex>"
    public required FrameSchema Schema    { get; init; }
    public string?              Family    { get; init; }
    public string?              Version   { get; init; }
    public string?              ParentId  { get; init; }
    public uint                 Ttl       { get; init; } = 3600;
}
```

Anchor ID 为内容寻址；参见 [`AnchorIdComputer`](#anchoridcomputer)。

### `DiffFrame`

```csharp
public sealed record DiffFrame : IFrame
{
    public required string                       AnchorId { get; init; }
    public required string                       ParentId { get; init; }
    public required IReadOnlyList<JsonPatchOperation> Patch { get; init; }
}

public sealed record JsonPatchOperation
{
    public required string       Op    { get; init; }   // "add"|"remove"|"replace"|"copy"|"move"|"test"
    public required string       Path  { get; init; }
    public JsonElement?          Value { get; init; }
    public string?               From  { get; init; }   // "move" 和 "copy" 必填
}
```

RFC 6902 JSON Patch 形状。顺序应用；对结果 SHA-256 以验证与 `AnchorId` 一致。

### `StreamFrame`

```csharp
public sealed record StreamFrame : IFrame
{
    public required string       AnchorRef { get; init; }
    public required ulong        Seq       { get; init; }   // 每流严格单调
    public required bool         IsFinal   { get; init; }
    public          JsonElement? Data      { get; init; }
    public          string?      StreamId  { get; init; }
}
```

当 `IsFinal = true` 时，编解码器将帧头 `FrameFlags.Final` bit 置位。

### `CapsFrame`

```csharp
public sealed record CapsFrame : IFrame
{
    public required string       AnchorRef { get; init; }
    public required uint         Count     { get; init; }
    public required JsonElement  Data      { get; init; }   // 数组或对象
    public          string?      NextToken { get; init; }   // 分页游标
}
```

`NwpClient.QueryAsync` 及类似结构化读取的响应信封。

### `ErrorFrame`

```csharp
public sealed record ErrorFrame : IFrame
{
    public required string Code    { get; init; }   // 如 "NCP-PAYLOAD-TOO-LARGE"
    public required string Message { get; init; }
    public          ushort? Status { get; init; }   // NPS 状态码，见 NpsStatusCodes
    public          JsonElement? Details { get; init; }
}
```

跨所有协议统一的错误信封。从保留的 `0xFE` 槽位分配,而非每个协议各自定义错误帧。

### `HelloFrame`

长连接使用的轻量保活 / 握手。携带协议版本和可选对端 NID。

### `AlignFrame`（已弃用）

为了向 pre-v0.3 的 NOP 流保持向后兼容而保留。新代码**必须**使用
`LabAcacia.NPS.NOP` 中的 `AlignStreamFrame` (0x43) —— 新帧绑定到 task ID
并验证发送者 NID,而 `AlignFrame` 不具备这些能力。

---

## 编解码器

### `NpsFrameCodec`

```csharp
namespace NPS.Core.Codecs;

public sealed class NpsFrameCodec
{
    public NpsFrameCodec(FrameRegistry registry, NpsCoreOptions? options = null);

    public byte[]  Encode<TFrame>(TFrame frame, EncodingTier? overrideTier = null) where TFrame : IFrame;
    public IFrame  Decode(ReadOnlySpan<byte> wire);
    public TFrame  Decode<TFrame>(ReadOnlySpan<byte> wire) where TFrame : IFrame;

    public static FrameHeader PeekHeader(ReadOnlySpan<byte> wire, out int headerSize);
}
```

行为：

1. **Tier 解析** —— `overrideTier` > `frame.PreferredTier` > `NpsCoreOptions.DefaultTier`。
2. **大小守卫** —— 若序列化 payload 超过 `NpsCoreOptions.MaxFramePayload`,
   `Encode` 抛出 `NpsCodecError` 并使用错误码 `NCP-PAYLOAD-TOO-LARGE`。
3. **扩展 flag** —— 若 `EnableExtendedFrameHeader` 为 `true` **或** payload 无法
   容纳于 16 位长度字段时,编解码器写入 8 字节帧头并置 EXT flag。
4. **解码分派** —— 首字节为 `FrameType` 判别符；编解码器在 `FrameRegistry` 中
   查找具体 CLR 类型并按 flag 字节指示的 tier 反序列化。
5. **未知帧类型**以错误码 `NCP-UNKNOWN-FRAME-TYPE` 抛 `NpsCodecError`。

`PeekHeader` 允许路由器在无需承担 payload 解码开销的情况下检视帧头 —— 对
做帧类型路由的边缘代理很有用。

### `Tier1JsonCodec` / `Tier2MsgPackCodec`

由 `NpsFrameCodec` 调用的更底层 tier 特定编解码器。

- `Tier1JsonCodec` 使用 `System.Text.Json`，`PropertyNamingPolicy = SnakeCaseLower`
  并采用大小写不敏感的反序列化器。`JsonElement` 字段深度拷贝,返回的帧不保留
  对线路缓冲区的引用。
- `Tier2MsgPackCodec` 使用 `MessagePack-CSharp` 搭配 `ContractlessStandardResolver`。

仅在高级场景（例如为大量相同形状的帧预计算 payload）才需直接使用。

---

## Anchor 缓存

### `AnchorIdComputer`

```csharp
namespace NPS.Core.Anchoring;

public static class AnchorIdComputer
{
    public static string Compute(FrameSchema schema);   // 返回 "sha256:<hex>"
}
```

确定性内容哈希：

1. 经过**目标 JCS 规范**的 JSON 遍历序列化 `schema`（RFC 8785 子集）—— 键按字典序,
   无空白,规范化数字格式。
2. 对 UTF-8 字节做 SHA-256。
3. 前缀 `"sha256:"` 并做 hex 编码。

保证跨进程和运行时按字节稳定。

### `AnchorFrameCache`

```csharp
namespace NPS.Core.Caching;

public sealed class AnchorFrameCache
{
    public AnchorFrameCache(Func<DateTime>? clock = null);

    public string       Set(AnchorFrame frame);                  // 返回 anchor_id
    public bool         TryGet(string anchorId, out AnchorFrame frame);
    public AnchorFrame  GetRequired(string anchorId);
    public void         Remove(string anchorId);
    public int          Count { get; }
}
```

语义：

- `Set` 从 schema 重新计算 `anchor_id`；若该 id 已有条目,缓存校验 schema 是否相等。
  不匹配时抛出 `NpsCodecError` 并使用错误码 `NCP-ANCHOR-POISON` —— 防止以相同 id
  使用恶意 schema 影子化一个可信的 schema。
- `TryGet` 与 `GetRequired` 遵循 `AnchorFrame.Ttl`。过期条目在访问时惰性驱逐。
- `GetRequired` 在条目缺失或过期时抛出 `NpsCodecError` 并使用错误码
  `NCP-ANCHOR-NOT-FOUND`。
- 可注入自定义 clock 以供单元测试。

---

## 帧注册表

### `FrameRegistry` / `FrameRegistryBuilder`

```csharp
namespace NPS.Core.Registry;

public sealed class FrameRegistry
{
    public Type?    GetType(FrameType frameType);
    public FrameType? GetFrameType(Type clrType);
    public IReadOnlyDictionary<FrameType, Type> All { get; }
}

public sealed class FrameRegistryBuilder
{
    public FrameRegistryBuilder Register<TFrame>(FrameType type) where TFrame : IFrame;
    public FrameRegistry        Build();
}
```

典型构造：

```csharp
var registry = new FrameRegistryBuilder()
    .AddNcp()           // 来自 NPS.Core
    .AddNwp()           // 来自 LabAcacia.NPS.NWP
    .AddNip()           // 来自 LabAcacia.NPS.NIP
    .AddNdp()           // 来自 LabAcacia.NPS.NDP
    .AddNop()           // 来自 LabAcacia.NPS.NOP
    .Build();
```

内部由 `FrozenDictionary<FrameType, Type>` 支持以实现快速查找。重复注册同一帧类型
会抛出 —— 这在组合时就能捕获意外错配,而非运行时才暴露。

每个协议包都提供扩展方法（`AddNcp`、`AddNwp`、……）,调用者很少需要手工注册
帧类型。

---

## 错误码、状态码与异常

### `NpsExceptions`

```csharp
namespace NPS.Core.Exceptions;

public class NpsException : Exception
{
    public string Code { get; }
    public NpsException(string code, string message, Exception? inner = null);
}

public sealed class NpsCodecError          : NpsException { /* 帧类型未知、payload 过大等 */ }
public sealed class AnchorNotFoundError    : NpsCodecError { }
public sealed class AnchorPoisonError      : NpsCodecError { }
```

每个异常都携带与 `spec/error-codes.md` 匹配的机器可读 `Code`。

### `NcpErrorCodes`

NCP 层错误常量。重点：

- `NCP-UNKNOWN-FRAME-TYPE`
- `NCP-INVALID-TIER`
- `NCP-PAYLOAD-TOO-LARGE`
- `NCP-HEADER-TRUNCATED`
- `NCP-ANCHOR-NOT-FOUND`
- `NCP-ANCHOR-POISON`

### `NpsStatusCodes`

双字节原生状态码及 HTTP 映射。规范：`spec/status-codes.md`。示例：

| 原生   | HTTP | 含义                           |
|--------|------|--------------------------------|
| 0x0000 | 200  | `Ok`                           |
| 0x0100 | 204  | `NoContent`                    |
| 0x0401 | 401  | `AuthRequired`                 |
| 0x0403 | 403  | `Forbidden`                    |
| 0x0404 | 404  | `NotFound`                     |
| 0x0413 | 413  | `PayloadTooLarge`              |
| 0x0422 | 422  | `SchemaInvalid`                |
| 0x0500 | 500  | `InternalError`                |

以 NPS 原生模式通信时使用这些 —— 而不是原始 HTTP 码。

---

## Patch 格式常量

```csharp
namespace NPS.Core.Frames;

public static class NcpPatchFormat
{
    public const string Add     = "add";
    public const string Remove  = "remove";
    public const string Replace = "replace";
    public const string Copy    = "copy";
    public const string Move    = "move";
    public const string Test    = "test";
}
```

匹配 RFC 6902；在以编程方式构建 `DiffFrame.Patch` 时使用。

---

## `AddNpsCore`

```csharp
namespace NPS.Core.Extensions;

public static class NpsCoreServiceExtensions
{
    public static IServiceCollection AddNpsCore(
        this IServiceCollection services,
        Action<NpsCoreOptions>? configure = null);
}

public sealed class NpsCoreOptions
{
    public EncodingTier DefaultTier              { get; set; } = EncodingTier.MsgPack;
    public uint         AnchorTtlSeconds         { get; set; } = 3600;
    public bool         AllowPlaintext           { get; set; } = false;
    public uint         MaxFramePayload          { get; set; } = FrameHeader.DefaultMaxPayload;
    public bool         EnableExtendedFrameHeader{ get; set; } = false;
}
```

注册以下单例：

| DI 服务                 | 具体类型                                |
|-------------------------|-----------------------------------------|
| `NpsCoreOptions`        | 来自 `configure` 回调                   |
| `FrameRegistry`         | 默认从 `NcpFrames` 构建                 |
| `NpsFrameCodec`         | 使用上述注册表 + 选项                   |
| `AnchorFrameCache`      | 默认实现                                |

`AllowPlaintext = false`（默认值）使编解码器**拒绝**在需要 Tier-2 的上下文（例如
调用者在 `AllowPlaintext = false` 的连接中请求生产安全编码）下对 `PreferredTier`
为 JSON 的帧进行编码。仅在开发环境中切换为 `true`。

---

## 最小端到端示例

```csharp
// 1. 构建注册表 + 编解码器
var registry = new FrameRegistryBuilder().AddNcp().Build();
var codec    = new NpsFrameCodec(registry);

// 2. 计算 anchor id 并缓存 schema
var schema = new FrameSchema
{
    Fields =
    [
        new SchemaField { Name = "id",    Type = "uint64",  Required = true },
        new SchemaField { Name = "price", Type = "decimal", Semantic = "commerce.price.usd" },
    ],
};
var cache    = new AnchorFrameCache();
var anchor   = new AnchorFrame
{
    AnchorId = AnchorIdComputer.Compute(schema),
    Schema   = schema,
    Ttl      = 3600,
};
cache.Set(anchor);

// 3. 编码 + 解码往返
var wire    = codec.Encode(anchor);
var decoded = codec.Decode<AnchorFrame>(wire);

Console.WriteLine(decoded.AnchorId == anchor.AnchorId);   // True
```
