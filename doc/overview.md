English | [中文版](./overview.cn.md)

# NPS .NET SDK — Overview

> Audience: library users building Memory Nodes, Agents, Orchestrators, or CA servers on the Neural Protocol Suite.
> Target framework: **.NET 10** (`<TargetFramework>net10.0</TargetFramework>`).

This document is the entry point for the `LabAcacia.NPS.*` NuGet packages. It walks through the package
layout, the protocol → library mapping, the dependency-injection pipeline, and the encoding tiers.
Per-package class-level reference lives in the sibling files:

| Package                  | Reference document                   |
|--------------------------|--------------------------------------|
| `LabAcacia.NPS.Core`     | [NPS.Core.md](./NPS.Core.md)         |
| `LabAcacia.NPS.NWP`      | [NPS.NWP.md](./NPS.NWP.md)           |
| `LabAcacia.NPS.NIP`      | [NPS.NIP.md](./NPS.NIP.md)           |
| `LabAcacia.NPS.NDP`      | [NPS.NDP.md](./NPS.NDP.md)           |
| `LabAcacia.NPS.NOP`      | [NPS.NOP.md](./NPS.NOP.md)           |

A Chinese long-form walk-through is available at [sdk-usage.cn.md](./sdk-usage.cn.md).

---

## 1. Package layout

The SDK is split along protocol boundaries. Dependencies fan out from `Core`:

```
LabAcacia.NPS.Core  ← wire format, codec, frame registry, anchor cache
      │
      ├── LabAcacia.NPS.NWP  ← Memory Node middleware, Query/Action frames, Neural Web Manifest
      ├── LabAcacia.NPS.NIP  ← Ed25519 identity, CA service, IdentFrame verifier
      ├── LabAcacia.NPS.NDP  ← Announce/Resolve/Graph frames, in-memory registry
      └── LabAcacia.NPS.NOP  ← Task DAG orchestrator, delegate dispatch, result aggregator
```

Rules:

- You always reference `LabAcacia.NPS.Core` — it owns the `NpsFrameCodec`, `FrameRegistry`, and
  `AnchorFrameCache`. Every other package builds on these.
- `LabAcacia.NPS.NIP` depends on `Core` only. `LabAcacia.NPS.NDP` depends on `Core` + `NIP` (because
  `NdpAnnounceValidator` calls `NipSigner`).
- `LabAcacia.NPS.NOP` depends on `Core` only; the delegate dispatch path is decoupled through
  `INopWorkerClient` so callers choose their own transport.

All packages are published as `LabAcacia.NPS.{suffix}` on NuGet (matching the GitHub org).

---

## 2. Protocol → library mapping

| Spec           | Version   | C# namespace root          | Frame types                                                    |
|----------------|-----------|----------------------------|----------------------------------------------------------------|
| NPS-1 NCP      | v0.4      | `NPS.Core.Frames`          | `Anchor` 0x01, `Diff` 0x02, `Stream` 0x03, `Caps` 0x04, `Error` 0xFE, `Hello` 0x00 |
| NPS-2 NWP      | v0.4      | `NPS.NWP.Frames`           | `Query` 0x10, `Action` 0x11                                     |
| NPS-3 NIP      | v0.2      | `NPS.NIP.Frames`           | `Ident` 0x20, `Trust` 0x21, `Revoke` 0x22                       |
| NPS-4 NDP      | v0.2      | `NPS.NDP.Frames`           | `Announce` 0x30, `Resolve` 0x31, `Graph` 0x32                   |
| NPS-5 NOP      | v0.3      | `NPS.NOP.Frames`           | `Task` 0x40, `Delegate` 0x41, `Sync` 0x42, `AlignStream` 0x43   |

`AlignFrame` (legacy NCP 0x05) is retained as a deprecated type; new code MUST use `AlignStreamFrame` (0x43).

---

## 3. The canonical DI pipeline

Every `ASP.NET Core` process using NPS follows the same shape. Method names are `Add*` for services
and `Use*` / `Map*` for the HTTP pipeline.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Core — codec, registry, anchor cache, options
builder.Services.AddNpsCore(o =>
{
    o.DefaultTier = EncodingTier.MsgPack;   // Tier-2 is the production default
    o.AnchorTtlSeconds = 3600;
    o.EnableExtendedFrameHeader = false;    // Flip on when you need >64 KiB payloads
});

// 2. NWP — Memory Node manifest + middleware (mount before Map)
builder.Services.AddNwp();
builder.Services.AddMemoryNode<ProductsProvider>(o =>
{
    o.NodeId = "urn:nps:node:api.example.com:products";
    o.DisplayName = "Products";
    o.Schema = new MemoryNodeSchema { /* fields … */ };
});

// 3. NIP — identity verifier for incoming agents (trust roots keyed by CA NID)
builder.Services.AddNipVerifier(o => o.TrustedIssuers = caBundle);

// 4. NDP — in-memory registry for federated discovery
builder.Services.AddNdp();

// 5. NOP — orchestrator (requires an INopWorkerClient implementation you supply)
builder.Services.AddSingleton<INopWorkerClient, MyHttpWorkerClient>();
builder.Services.AddNopOrchestrator();

var app = builder.Build();

app.UseRouting();
app.UseMemoryNode<ProductsProvider>();      // mounts /.nwm, /.schema, /query, /stream
app.MapNipCa();                             // optional — only when this process is itself a CA

app.Run();
```

If you only need the wire format (e.g. a console test client), `AddNpsCore` alone is sufficient —
construct an `NpsFrameCodec` directly via `sp.GetRequiredService<NpsFrameCodec>()`.

---

## 4. Encoding tiers

| Tier | Byte value | `EncodingTier` | When to use                              |
|------|------------|----------------|------------------------------------------|
| 1    | `0x00`     | `JSON`         | Development, debugging, cross-runtime interop |
| 2    | `0x01`     | `MsgPack`      | **Production default** — ~60 % smaller on real workloads |
| 3    | `0x02`     | reserved       | Do **not** use; value reserved pending future spec       |

The selection rule is:

1. `NpsCoreOptions.DefaultTier` sets the process-wide default.
2. Each frame type may override via `IFrame.PreferredTier`.
3. A per-call override: `codec.Encode(frame, EncodingTier.JSON)` wins over both of the above.

The byte is written into the frame header so decoders always know how to read the payload — no
sniffing required.

---

## 5. Frame header shape

Every frame on the wire is prefixed by a compact header. The SDK handles this transparently
via `NpsFrameCodec`; you only need the details if you are inspecting frames on the wire.

```
┌───────────┬──────────┬───────────┬──────────────────┐
│ FrameType │ Tier+Ext │ PayloadLn │ Payload          │
│ 1 byte    │ 1 byte   │ 2 or 4 B  │ variable         │
└───────────┴──────────┴───────────┴──────────────────┘
```

- Default header: **4 bytes** (2-byte payload length, ≤ 65 535 bytes).
- Extended header (EXT flag set): **8 bytes** (4-byte payload length, ≤ 4 GiB).
- The codec sets EXT automatically when your payload would overflow the 2-byte length field, and
  rejects encoding attempts that exceed `NpsCoreOptions.MaxFramePayload`.

See [NPS.Core.md §`FrameHeader`](./NPS.Core.md#frameheader) for the exact layout and bit mask.

---

## 6. Threading and lifetimes

| Service                   | DI lifetime | Thread-safe?                             |
|---------------------------|-------------|------------------------------------------|
| `NpsFrameCodec`           | Singleton   | Yes                                      |
| `FrameRegistry`           | Singleton   | Yes (FrozenDictionary)                   |
| `AnchorFrameCache`        | Singleton   | Yes (lock-protected)                     |
| `NipKeyManager`           | Singleton   | Private key load is one-shot; signing is thread-safe |
| `NipIdentVerifier`        | Singleton   | Yes                                      |
| `NipCaService`            | Singleton   | Yes (async, store-backed)                |
| `InMemoryNdpRegistry`     | Singleton   | Yes (internal lock)                      |
| `NopOrchestrator`         | Singleton   | Yes (per-task CTS tracked in dictionary) |
| `MemoryNodeMiddleware`    | Transient   | Per-request                              |
| `IMemoryNodeProvider`     | Scoped      | User-defined; follow ASP.NET Core rules  |

---

## 7. Where to go next

- New to NPS? Read [`spec/NPS-0-Overview.md`](https://github.com/labacacia/NPS/blob/main/NPS-0-Overview.md)
  first to understand why the protocol suite exists.
- Building a Memory Node? See [NPS.NWP.md](./NPS.NWP.md#memory-node-middleware).
- Running a CA? See [NPS.NIP.md](./NPS.NIP.md#nipcaservice).
- Composing multi-agent tasks? See [NPS.NOP.md](./NPS.NOP.md#noporchestrator).
