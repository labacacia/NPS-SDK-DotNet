English | [中文版](./NPS.Core.cn.md)

# `LabAcacia.NPS.Core` — Class and Method Reference

> Root namespace: `NPS.Core`
> NuGet: `LabAcacia.NPS.Core`
> Spec: [NPS-1 NCP v0.7](https://github.com/labacacia/NPS/blob/main/NPS-1-NCP.md)

This package owns the wire format, the frame codec, the frame type registry, the anchor cache, and
all shared exceptions / error codes. Every other package in the SDK depends on it.

---

## Table of contents

- [Frames](#frames)
  - [`IFrame`](#iframe)
  - [`FrameType`](#frametype)
  - [`FrameFlags`](#frameflags)
  - [`FrameHeader`](#frameheader)
  - [`FrameSchema` / `SchemaField`](#frameschema--schemafield)
- [NCP frames (`NPS.Core.Frames.Ncp`)](#ncp-frames)
  - [`AnchorFrame`](#anchorframe)
  - [`DiffFrame` + `JsonPatchOperation`](#diffframe)
  - [`StreamFrame`](#streamframe)
  - [`CapsFrame`](#capsframe)
  - [`ErrorFrame`](#errorframe)
  - [`HelloFrame`](#helloframe)
  - [`AlignFrame` (deprecated)](#alignframe-deprecated)
- [Codec](#codec)
  - [`NpsFrameCodec`](#npsframecodec)
  - [`Tier1JsonCodec` / `Tier2MsgPackCodec`](#tier1jsoncodec--tier2msgpackcodec)
- [Anchor cache](#anchor-cache)
  - [`AnchorIdComputer`](#anchoridcomputer)
  - [`AnchorFrameCache`](#anchorframecache)
- [Frame registry](#frame-registry)
  - [`FrameRegistry` / `FrameRegistryBuilder`](#frameregistry--frameregistrybuilder)
- [Error codes, status codes, and exceptions](#error-codes-status-codes-and-exceptions)
- [Patch format constants](#patch-format-constants)
- [DI extension `AddNpsCore`](#addnpscore)

---

## Frames

### `IFrame`

```csharp
namespace NPS.Core.Frames;

public interface IFrame
{
    FrameType    FrameType     { get; }
    EncodingTier PreferredTier { get; }
}
```

Every NPS frame (across all protocols) implements this interface.

| Member          | Purpose                                                                       |
|-----------------|-------------------------------------------------------------------------------|
| `FrameType`     | Byte discriminator written into the frame header (see [`FrameType`](#frametype)). |
| `PreferredTier` | Per-frame encoding preference. Overridden by the `overrideTier` parameter of the codec. |

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
    Align       = 0x05,   // deprecated
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

Authoritative: `spec/frame-registry.yaml`. The registry namespace is carved into 16-byte
blocks per protocol — new frame types for NCP add at `0x06+`, for NOP at `0x44+`, etc.

### `FrameFlags`

```csharp
[Flags]
public enum FrameFlags : byte
{
    Tier1Json    = 0x00,   // low bit clear
    Tier2MsgPack = 0x01,   // low bit set
    Final        = 0x04,   // terminal stream chunk
    Encrypted    = 0x08,   // payload is NIP-encrypted
    Ext          = 0x80,   // extended header — 4-byte payload length
}
```

The flag byte is the second byte of the frame header. `Tier1Json` and `Tier2MsgPack` are mutually
exclusive (low bit). `Final`, `Encrypted`, and `Ext` are independent bits.

### `FrameHeader`

```csharp
public readonly record struct FrameHeader(
    FrameType    FrameType,
    EncodingTier Tier,
    FrameFlags   Flags,
    uint         PayloadLength,
    bool         IsExtended);
```

#### Constants

| Constant                | Value              |
|-------------------------|--------------------|
| `FrameHeader.DefaultSize`   | `4`                |
| `FrameHeader.ExtendedSize`  | `8`                |
| `FrameHeader.DefaultMaxPayload` | `ushort.MaxValue` (65 535) |
| `FrameHeader.ExtendedMaxPayload` | `uint.MaxValue`            |

#### `static FrameHeader Parse(ReadOnlySpan<byte> source, out int bytesRead)`

Parses a header from the beginning of `source`. `bytesRead` returns either 4 or 8 depending on
whether the EXT flag was set. Throws `NpsCodecError` if:

- `source.Length < 4`.
- EXT is set but `source.Length < 8`.

#### `int WriteTo(Span<byte> destination)`

Serialises the header to `destination`, returning the number of bytes written (4 or 8). Throws
`ArgumentException` when the buffer is too small.

### `FrameSchema` / `SchemaField`

```csharp
public sealed record FrameSchema
{
    public IReadOnlyList<SchemaField> Fields { get; init; } = [];
    public string?                    Family { get; init; }   // optional schema family tag
}

public sealed record SchemaField
{
    public required string Name     { get; init; }
    public required string Type     { get; init; }            // e.g. "uint64", "string", "decimal"
    public string?         Semantic { get; init; }            // e.g. "commerce.price.usd"
    public bool            Required { get; init; }
    public bool            PrimaryKey { get; init; }
    public bool            Nullable { get; init; }
    public JsonElement?    Default  { get; init; }
    public string?         Description { get; init; }
}
```

Schemas are embedded inside `AnchorFrame` and referenced by `anchor_id`.

---

## NCP frames

All NCP frames live in `NPS.Core.Frames` (top-level `namespace NPS.Core.Frames.Ncp` is an internal
organisational detail — the public types are exposed at `NPS.Core.Frames.*`).

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

Anchor IDs are content-addressed; see [`AnchorIdComputer`](#anchoridcomputer).

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
    public string?               From  { get; init; }   // required for "move" and "copy"
}
```

RFC 6902 JSON Patch shape. Apply sequentially; SHA-256 the result to verify against `AnchorId`.

### `StreamFrame`

```csharp
public sealed record StreamFrame : IFrame
{
    public required string       AnchorRef { get; init; }
    public required ulong        Seq       { get; init; }   // strictly monotonic per stream
    public required bool         IsFinal   { get; init; }
    public          JsonElement? Data      { get; init; }
    public          string?      StreamId  { get; init; }
}
```

The `FrameFlags.Final` bit in the header is set by the codec when `IsFinal = true`.

### `CapsFrame`

```csharp
public sealed record CapsFrame : IFrame
{
    public required string       AnchorRef { get; init; }
    public required uint         Count     { get; init; }
    public required JsonElement  Data      { get; init; }   // array or object
    public          string?      NextToken { get; init; }   // pagination cursor
}
```

Response envelope for `NwpClient.QueryAsync` and similar structured reads.

### `ErrorFrame`

```csharp
public sealed record ErrorFrame : IFrame
{
    public required string Code    { get; init; }   // e.g. "NCP-PAYLOAD-TOO-LARGE"
    public required string Message { get; init; }
    public          ushort? Status { get; init; }   // NPS status code, see NpsStatusCodes
    public          JsonElement? Details { get; init; }
}
```

Unified error envelope across all protocols. Allocated from the reserved `0xFE` slot rather than
each protocol defining its own error frame.

### `HelloFrame`

Lightweight keep-alive / handshake used by long-lived connections. Carries protocol version and
optional peer NID.

### `AlignFrame` (deprecated)

Kept for backwards compatibility with pre-v0.3 NOP streams. New code MUST use
`AlignStreamFrame` (0x43) from `LabAcacia.NPS.NOP` — the new frame binds to a task ID and
verifies the sender NID, which `AlignFrame` does not.

---

## Codec

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

Behaviour:

1. **Tier resolution** — `overrideTier` > `frame.PreferredTier` > `NpsCoreOptions.DefaultTier`.
2. **Size guard** — if the serialised payload is larger than `NpsCoreOptions.MaxFramePayload`,
   `Encode` throws `NpsCodecError` with code `NCP-PAYLOAD-TOO-LARGE`.
3. **Extended flag** — if `EnableExtendedFrameHeader` is `true` **or** the payload would not fit
   in a 16-bit length field, the codec writes an 8-byte header with the EXT flag set.
4. **Decode dispatch** — the first byte is the `FrameType` discriminator; the codec looks up the
   concrete CLR type in `FrameRegistry` and deserialises using the tier indicated by the flag byte.
5. **Unknown frame types** throw `NpsCodecError` with code `NCP-UNKNOWN-FRAME-TYPE`.

`PeekHeader` lets routers inspect the header without paying the payload decode cost — useful on
edge proxies doing frame-type routing.

### `Tier1JsonCodec` / `Tier2MsgPackCodec`

Lower-level tier-specific codecs invoked by `NpsFrameCodec`.

- `Tier1JsonCodec` uses `System.Text.Json` with `PropertyNamingPolicy = SnakeCaseLower` and a case-
  insensitive deserialiser. `JsonElement` fields are copied deeply so the returned frame doesn't
  retain a reference to the wire buffer.
- `Tier2MsgPackCodec` uses `MessagePack-CSharp` with `ContractlessStandardResolver`.

Direct use is only needed in advanced scenarios (e.g. pre-computing payloads for many
identically-shaped frames).

---

## Anchor cache

### `AnchorIdComputer`

```csharp
namespace NPS.Core.Anchoring;

public static class AnchorIdComputer
{
    public static string Compute(FrameSchema schema);   // returns "sha256:<hex>"
}
```

Deterministic content hash:

1. Serialise `schema` via a targeted **JCS-canonical** JSON pass (RFC 8785 subset) — keys lexically
   sorted, no whitespace, canonical number formatting.
2. SHA-256 the UTF-8 bytes.
3. Prefix with `"sha256:"` and hex-encode.

Guarantees byte-for-byte stability across processes and runtimes.

### `AnchorFrameCache`

```csharp
namespace NPS.Core.Caching;

public sealed class AnchorFrameCache
{
    public AnchorFrameCache(Func<DateTime>? clock = null);

    public string       Set(AnchorFrame frame);                  // returns anchor_id
    public bool         TryGet(string anchorId, out AnchorFrame frame);
    public AnchorFrame  GetRequired(string anchorId);
    public void         Remove(string anchorId);
    public int          Count { get; }
}
```

Semantics:

- `Set` recomputes the `anchor_id` from the schema; if an entry already exists for that id,
  the cache verifies schema equality. A mismatch raises an `NpsCodecError` with code
  `NCP-ANCHOR-POISON` — this protects against attempts to shadow a trusted schema with a
  malicious one using the same id.
- `TryGet` and `GetRequired` honour `AnchorFrame.Ttl`. Expired entries are evicted lazily on
  access.
- `GetRequired` throws `NpsCodecError` with code `NCP-ANCHOR-NOT-FOUND` when the entry is
  absent or expired.
- A custom clock can be injected for unit tests.

---

## Frame registry

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

Canonical construction:

```csharp
var registry = new FrameRegistryBuilder()
    .AddNcp()           // from NPS.Core
    .AddNwp()           // from LabAcacia.NPS.NWP
    .AddNip()           // from LabAcacia.NPS.NIP
    .AddNdp()           // from LabAcacia.NPS.NDP
    .AddNop()           // from LabAcacia.NPS.NOP
    .Build();
```

Internally backed by a `FrozenDictionary<FrameType, Type>` for fast lookup. Registering the same
frame type twice throws; this catches accidental mis-wiring at composition time rather than at
runtime.

Each protocol package ships an extension method (`AddNcp`, `AddNwp`, …) so callers rarely register
frame types by hand.

---

## Error codes, status codes, and exceptions

### `NpsExceptions`

```csharp
namespace NPS.Core.Exceptions;

public class NpsException : Exception
{
    public string Code { get; }
    public NpsException(string code, string message, Exception? inner = null);
}

public sealed class NpsCodecError          : NpsException { /* frame type unknown, payload too large, etc. */ }
public sealed class AnchorNotFoundError    : NpsCodecError { }
public sealed class AnchorPoisonError      : NpsCodecError { }
```

Every exception carries a machine-readable `Code` matching `spec/error-codes.md`.

### `NcpErrorCodes`

Constants for NCP-layer errors. Highlights:

- `NCP-UNKNOWN-FRAME-TYPE`
- `NCP-INVALID-TIER`
- `NCP-PAYLOAD-TOO-LARGE`
- `NCP-HEADER-TRUNCATED`
- `NCP-ANCHOR-NOT-FOUND`
- `NCP-ANCHOR-POISON`

### `NpsStatusCodes`

Two-byte native status codes with an HTTP mapping. Spec: `spec/status-codes.md`. Examples:

| Native | HTTP | Meaning                       |
|--------|------|-------------------------------|
| 0x0000 | 200  | `Ok`                          |
| 0x0100 | 204  | `NoContent`                   |
| 0x0401 | 401  | `AuthRequired`                |
| 0x0403 | 403  | `Forbidden`                   |
| 0x0404 | 404  | `NotFound`                    |
| 0x0413 | 413  | `PayloadTooLarge`             |
| 0x0422 | 422  | `SchemaInvalid`               |
| 0x0500 | 500  | `InternalError`               |

Use these — not raw HTTP codes — when speaking native NPS mode.

---

## Patch format constants

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

Matches RFC 6902; intended for use when building `DiffFrame.Patch` programmatically.

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

Registers these singletons:

| DI service              | Concrete type                          |
|-------------------------|----------------------------------------|
| `NpsCoreOptions`        | From `configure` callback              |
| `FrameRegistry`         | Built from `NcpFrames` by default      |
| `NpsFrameCodec`         | Uses the registry + options above      |
| `AnchorFrameCache`      | Default implementation                 |

`AllowPlaintext = false` (the default) makes the codec **refuse** to encode a frame whose
`PreferredTier` is JSON over a context that requires Tier-2 (e.g. when the caller has asked
for a production-safe encoding in a connection with `AllowPlaintext = false`). Flip to `true`
in development only.

---

## Minimal end-to-end sample

```csharp
// 1. Build registry + codec
var registry = new FrameRegistryBuilder().AddNcp().Build();
var codec    = new NpsFrameCodec(registry);

// 2. Compute an anchor id and cache the schema
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

// 3. Encode + decode round-trip
var wire    = codec.Encode(anchor);
var decoded = codec.Decode<AnchorFrame>(wire);

Console.WriteLine(decoded.AnchorId == anchor.AnchorId);   // True
```
