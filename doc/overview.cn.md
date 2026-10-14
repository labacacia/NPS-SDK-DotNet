[English Version](./overview.md) | 中文版

# NPS .NET SDK — 总览

> 适用对象：基于 Neural Protocol Suite 构建 Memory Node、Agent、Orchestrator 或 CA Server 的库使用者。
> 目标框架：**.NET 10**（`<TargetFramework>net10.0</TargetFramework>`）。

本文档是 `LabAcacia.NPS.*` NuGet 包的入口。它介绍包结构、协议到库的映射、依赖注入（DI）管线以及编码分层。
每个包的类级参考请参见同目录其他文件：

| 包                        | 参考文档                              |
|---------------------------|---------------------------------------|
| `LabAcacia.NPS.Core`      | [NPS.Core.cn.md](./NPS.Core.cn.md)    |
| `LabAcacia.NPS.NWP`       | [NPS.NWP.cn.md](./NPS.NWP.cn.md)      |
| `LabAcacia.NPS.NIP`       | [NPS.NIP.cn.md](./NPS.NIP.cn.md)      |
| `LabAcacia.NPS.NDP`       | [NPS.NDP.cn.md](./NPS.NDP.cn.md)      |
| `LabAcacia.NPS.NOP`       | [NPS.NOP.cn.md](./NPS.NOP.cn.md)      |

中文长文教程参见 [sdk-usage.cn.md](./sdk-usage.cn.md)。

---

## 1. 包结构

SDK 沿协议边界拆分，依赖从 `Core` 向外发散：

```
LabAcacia.NPS.Core  ← 线缆格式、编解码、帧注册表、AnchorFrame 缓存
      │
      ├── LabAcacia.NPS.NWP  ← Memory Node 中间件、Query/Action 帧、Neural Web Manifest
      ├── LabAcacia.NPS.NIP  ← Ed25519 身份、CA 服务、IdentFrame 验证
      ├── LabAcacia.NPS.NDP  ← Announce/Resolve/Graph 帧、内存注册表
      └── LabAcacia.NPS.NOP  ← Task DAG 编排、委托分发、结果聚合
```

规则：

- 任何情况下都必须引用 `LabAcacia.NPS.Core` —— 它承载 `NpsFrameCodec`、`FrameRegistry` 和
  `AnchorFrameCache`，其他所有包都基于它。
- `LabAcacia.NPS.NIP` 仅依赖 `Core`。`LabAcacia.NPS.NDP` 依赖 `Core` + `NIP`（因为
  `NdpAnnounceValidator` 调用 `NipSigner`）。
- `LabAcacia.NPS.NOP` 仅依赖 `Core`；委托分发路径通过 `INopWorkerClient` 解耦，调用方自行选择传输层。

所有包在 NuGet 发布为 `LabAcacia.NPS.{suffix}`（与 GitHub 组织保持一致）。

---

## 2. 协议到库的映射

| 规范            | 版本      | C# 命名空间根              | 帧类型                                                          |
|-----------------|-----------|----------------------------|-----------------------------------------------------------------|
| NPS-1 NCP       | v0.4      | `NPS.Core.Frames`          | `Anchor` 0x01、`Diff` 0x02、`Stream` 0x03、`Caps` 0x04、`Error` 0xFE、`Hello` 0x00 |
| NPS-2 NWP       | v0.4      | `NPS.NWP.Frames`           | `Query` 0x10、`Action` 0x11                                     |
| NPS-3 NIP       | v0.2      | `NPS.NIP.Frames`           | `Ident` 0x20、`Trust` 0x21、`Revoke` 0x22                       |
| NPS-4 NDP       | v0.2      | `NPS.NDP.Frames`           | `Announce` 0x30、`Resolve` 0x31、`Graph` 0x32                   |
| NPS-5 NOP       | v0.3      | `NPS.NOP.Frames`           | `Task` 0x40、`Delegate` 0x41、`Sync` 0x42、`AlignStream` 0x43   |

`AlignFrame`（旧 NCP 0x05）作为已弃用类型保留；新代码必须使用 `AlignStreamFrame`（0x43）。

---

## 3. 标准 DI 管线

每个使用 NPS 的 ASP.NET Core 进程都遵循同一种形状。服务用 `Add*` 方法，HTTP 管线用 `Use*` / `Map*`。

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Core —— 编解码、注册表、AnchorFrame 缓存、Options
builder.Services.AddNpsCore(o =>
{
    o.DefaultTier = EncodingTier.MsgPack;   // Tier-2 是生产默认
    o.AnchorTtlSeconds = 3600;
    o.EnableExtendedFrameHeader = false;    // 需要 >64 KiB 负载时打开
});

// 2. NWP —— Memory Node manifest + 中间件（Map 之前挂载）
builder.Services.AddNwp();
builder.Services.AddMemoryNode<ProductsProvider>(o =>
{
    o.NodeId = "urn:nps:node:api.example.com:products";
    o.DisplayName = "Products";
    o.Schema = new MemoryNodeSchema { /* 字段 … */ };
});

// 3. NIP —— 对入站 Agent 做身份验证（信任根按 CA NID 索引）
builder.Services.AddNipVerifier(o => o.TrustedIssuers = caBundle);

// 4. NDP —— 联邦发现用内存注册表
builder.Services.AddNdp();

// 5. NOP —— 编排器（需要你提供一个 INopWorkerClient 实现）
builder.Services.AddSingleton<INopWorkerClient, MyHttpWorkerClient>();
builder.Services.AddNopOrchestrator();

var app = builder.Build();

app.UseRouting();
app.UseMemoryNode<ProductsProvider>();      // 挂载 /.nwm、/.schema、/query、/stream
app.MapNipCa();                             // 可选 —— 仅当本进程本身就是 CA 时

app.Run();
```

如果你只需要线缆格式（例如一个控制台测试客户端），单独 `AddNpsCore` 就够了 ——
通过 `sp.GetRequiredService<NpsFrameCodec>()` 直接拿到 `NpsFrameCodec`。

---

## 4. 编码分层

| Tier | 字节值     | `EncodingTier` | 使用场景                                  |
|------|------------|----------------|-------------------------------------------|
| 1    | `0x00`     | `JSON`         | 开发、调试、跨运行时互操作                |
| 2    | `0x01`     | `MsgPack`      | **生产默认** —— 真实负载约小 60%          |
| 3    | `0x02`     | 保留           | **请勿使用**；等待后续规范分配            |

选择规则：

1. `NpsCoreOptions.DefaultTier` 设置进程级默认。
2. 每种帧类型可以通过 `IFrame.PreferredTier` 覆盖。
3. 单次调用覆盖：`codec.Encode(frame, EncodingTier.JSON)` 优先级最高。

该字节会写入帧头，所以解码方总能知道如何读取负载 —— 无需嗅探。

---

## 5. 帧头结构

线缆上的每一帧都有一个紧凑的头部。SDK 通过 `NpsFrameCodec` 透明处理；只有当你需要直接检查
线缆字节时才关心细节。

```
┌───────────┬──────────┬───────────┬──────────────────┐
│ FrameType │ Tier+Ext │ PayloadLn │ Payload          │
│ 1 byte    │ 1 byte   │ 2 or 4 B  │ variable         │
└───────────┴──────────┴───────────┴──────────────────┘
```

- 默认帧头：**4 字节**（2 字节负载长度，≤ 65 535 字节）。
- 扩展帧头（EXT 置位）：**8 字节**（4 字节负载长度，≤ 4 GiB）。
- 当负载将溢出 2 字节长度域时，编解码器会自动置 EXT，并拒绝超过
  `NpsCoreOptions.MaxFramePayload` 的编码。

精确布局与位掩码见 [NPS.Core.cn.md §`FrameHeader`](./NPS.Core.cn.md#frameheader)。

---

## 6. 线程与生命周期

| 服务                       | DI 生命周期 | 线程安全                                         |
|----------------------------|-------------|--------------------------------------------------|
| `NpsFrameCodec`            | Singleton   | 是                                               |
| `FrameRegistry`            | Singleton   | 是（FrozenDictionary）                           |
| `AnchorFrameCache`         | Singleton   | 是（锁保护）                                     |
| `NipKeyManager`            | Singleton   | 私钥加载一次性；签名线程安全                     |
| `NipIdentVerifier`         | Singleton   | 是                                               |
| `NipCaService`             | Singleton   | 是（async，基于存储）                            |
| `InMemoryNdpRegistry`      | Singleton   | 是（内部锁）                                     |
| `NopOrchestrator`          | Singleton   | 是（每个 task 的 CTS 维护在字典中）              |
| `MemoryNodeMiddleware`     | Transient   | 每请求一个                                       |
| `IMemoryNodeProvider`      | Scoped      | 用户自定义；遵循 ASP.NET Core 规则               |

---

## 7. 后续阅读

- 初识 NPS？先读 [`spec/NPS-0-Overview.cn.md`](https://github.com/labacacia/NPS/blob/main/NPS-0-Overview.cn.md)
  以理解协议套件存在的原因。
- 要构建 Memory Node？参见 [NPS.NWP.cn.md](./NPS.NWP.cn.md#memory-node-%E4%B8%AD%E9%97%B4%E4%BB%B6)。
- 要运行 CA？参见 [NPS.NIP.cn.md](./NPS.NIP.cn.md#nipcaservice)。
- 要组合多 Agent 任务？参见 [NPS.NOP.cn.md](./NPS.NOP.cn.md#noporchestrator)。
