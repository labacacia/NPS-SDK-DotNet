[English Version](./README.md) | 中文版

# NPS .NET 参考实现

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/badge/release-v1.0.0--alpha.18-orange.svg)](CHANGELOG.cn.md)
[![NCP](https://img.shields.io/badge/NCP-v0.11-5b8cff.svg)]()
[![NWP](https://img.shields.io/badge/NWP-v0.21-4af0b0.svg)]()
[![NIP](https://img.shields.io/badge/NIP-v0.14-7b61ff.svg)]()
[![NDP](https://img.shields.io/badge/NDP-v0.12-f0a050.svg)]()
[![NOP](https://img.shields.io/badge/NOP-v0.9-ff8c42.svg)]()

C# / .NET 10 Neural Protocol Suite 参考实现。

## Alpha.18 可移植 Profile

本发布版实现五个共享协议 Profile，并执行
[`spec/conformance`](spec/conformance/) 下的语言无关 fixture：

- NCP 0.11 有界原生服务握手与确定性 Caps 协商。
- NWP 0.21 可移植 Node/Bridge serving 与 Bridge 生命周期。
- NIP 0.14 可移植 CA、实时吊销、签名 CRL 与验证策略。
- NDP 0.12 签名 Announce 准入及 registry 冲突/liveness 策略。
- NOP 0.9 确定性编排、callback 安全、委派、租约与 CR-0007 runtime 决策。

## NuGet 包

| 包名 | 版本 | 说明 |
|------|------|------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.19 | 共享帧类型（AnchorFrame、DiffFrame、StreamFrame、CapsFrame、HelloFrame、ErrorFrame）、JSON/MsgPack 编解码、AnchorFrame 缓存、帧注册表 |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.19 | Neural Web Protocol — NWM 清单、Query / Action / Subscribe / Diff 帧、包含 `llm.complete` 的 typed Action/frame payload helper、Memory / Action / Complex / Anchor / Bridge Node 中间件，以及 native-mode serving |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.19 | NWP Anchor Node：把 ActionFrame 无状态翻译到 NOP TaskFrame 的 AaaS 入口；`AnchorNodeClient` 支持 `topology.snapshot` / `topology.stream` 拓扑查询 |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.19 | NWP Bridge Node：NPS 帧到非 NPS 协议的出站 dispatcher，以及入站 MCP/A2A server bridge |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.19 | Neural Identity Protocol — CA、Ed25519 密钥生成、IdentFrame 签发 / 吊销、类型化远程 CA client、OCSP、CRL；X.509 + ACME `agent-01` challenge（RFC-0002 原型） |
| `LabAcacia.NPS.NIP.Storage.Sqlite` | 1.0.0-alpha.19 | 嵌入式 / 自托管 NIP CA 的 SQLite 存储后端 |
| `LabAcacia.NPS.NIP.Storage.Postgres` | 1.0.0-alpha.19 | 服务化 NIP CA 的 PostgreSQL 存储后端 |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.19 | Neural Discovery Protocol — announce / resolve 帧、内存注册表、Ed25519 校验 |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.19 | Neural Orchestration Protocol — Task / Delegate / Sync / AlignStream 帧、DAG 校验器、编排引擎 |
| `LabAcacia.NPS.Daemon.Observability` | 1.0.0-alpha.19 | JSON 日志、传输无关 health/readiness 渲染器、ASP.NET endpoint helper、Prometheus metrics、优雅关闭 |
| `LabAcacia.NPS.Conformance` | 1.0.0-alpha.19 | Node L1/L2 conformance case catalog、run manifest model 与 CI validation helper |

## 开源与 NPS Cloud 边界

| 领域 | 开源包支持 | NPS Cloud / 商业支持 |
|------|------------|----------------------|
| NCP/NWP/NDP/NOP 帧编解码与进程内服务 | 已包含 | 托管运行与运维 |
| NIP CA、IdentFrame 签发、吊销、OCSP/CRL、X.509 原型 | 已包含 | 托管 CA 运营与策略自动化 |
| TrustFrame 解析与基础验证 | 已包含：`TrustFrame` + `TrustFrameValidator`，适用于显式固定 grantor anchor 的自托管场景 | 托管多 CA federation、trust-anchor discovery、吊销 feed、商业 trust-chain policy |
| Daemon observability | 已包含：传输无关渲染器 + ASP.NET 映射 helper | 托管监控 / SLO 集成 |

## 快速开始

### Core Codec

```csharp
using NPS.Core.Codecs;
using NPS.Core.Frames.Ncp;

var codec = NpsFrameCodec.CreateDefault();
var frame = new HelloFrame { Version = "1.0", NodeId = "urn:nps:demo", Capabilities = ["ncp"] };
var wire = codec.Encode(frame);
var header = codec.Peek(wire);
var decoded = codec.Decode(wire);
```

`LabAcacia.NPS.Core` 的内置 Tier-2 codec 使用 MessagePack-CSharp 源生成 formatter；
内置 registry 与上层协议注册 helper 均兼容 NativeAOT。宿主如果
需要替换二进制 codec，可以实现 `IFrameCodec`，并用自定义 codec 实例构造
`NpsFrameCodec`；DI helper 默认注册 JSON + MessagePack 组合。

### 依赖注入

```csharp
using Microsoft.Extensions.DependencyInjection;
using NPS.Core.Extensions;

var services = new ServiceCollection()
    .AddNpsCore(options => options.EnableExtendedFrameHeader = true);
```

### NIP 基础 TrustFrame 验证

```csharp
using NPS.NIP.Verification;

var result = TrustFrameValidator.Validate(trustFrame, new TrustFrameValidationContext
{
    TrustedGrantors = new HashSet<string> { "urn:nps:org-a:ca" },
    ExpectedGranteeCa = "urn:nps:org-b:ca",
    RequiredCapabilities = ["nwp:query"],
    TargetNodePath = "nwp://api.example.com/products",
});
```

### 非 Kestrel observability

```csharp
using NPS.Daemon.Observability.HealthChecks;

var health = HealthProbeRenderer.RenderHealthz();
var ready = await HealthProbeRenderer.RenderReadyzAsync(readinessProbes, cancellationToken);
```

### ASP.NET observability endpoints

```csharp
using NPS.Daemon.Observability;

builder.Services.AddNpsObservability();
app.MapNpsObservability();
```

### 存储包

```csharp
using NPS.NIP.Extensions;

services.AddNipCaWithSqlite(
    options => ConfigureCa(options),
    "Data Source=nip-ca.db");

services.AddNipCaWithPostgres(options =>
{
    ConfigureCa(options);
    options.ConnectionString = postgresConnectionString;
});
```

### Bridge 与 Ingress 包

`LabAcacia.NPS.NWP.Bridge` 覆盖 Bridge Node 的两个方向。`BridgeNode`
和 `IBridgeDispatcher` 处理 NPS 到外部协议的出站调用，内置 HTTP/HTTPS、
gRPC JSON unary、MCP JSON-RPC、A2A JSON-RPC dispatcher。`McpServerBridge`
和 `A2aServerBridge` 把本地 NPS action 暴露给外部 MCP/A2A 客户端。
独立的 `LabAcacia.McpIngress`、`LabAcacia.A2aIngress`、`LabAcacia.GrpcIngress`
兼容边界由 release workflow 打包；只有完成对应版本的 NuGet publish 后才可直接消费。

```csharp
using System.Text.Json;
using NPS.NWP.Bridge;
using NPS.NWP.Frames;

var registry = BridgeDispatcherRegistry.CreateDefault(new HttpClient());
var bridge = new BridgeNode(registry);

using var parameters = JsonDocument.Parse("""
{
  "bridge_target": {
    "protocol": "http",
    "endpoint": "https://api.example.test/run",
    "extras": {
      "method": "POST",
      "allowed_prefixes": [ "https://api.example.test/" ],
      "headers": { "x-agent": "nps" }
    }
  },
  "body": { "task": "sync" }
}
""");

var response = await bridge.DispatchAsync(new ActionFrame
{
    ActionId = "bridge.dispatch",
    Params = parameters.RootElement.Clone(),
    TimeoutMs = 5000
});
```

ASP.NET host 可以把同一个 Bridge Node 暴露成 NWP endpoint：

```csharp
builder.Services.AddBridgeNode(
    options =>
    {
        options.NodeId = "bridge-1";
        options.PathPrefix = "/bridge";
    },
    dispatchers =>
    {
        // 可选：dispatchers.Register(new GrpcBridgeDispatcher(...));
    });

app.UseBridgeNode(); // GET /bridge/.nwm, GET /bridge/actions, POST /bridge/invoke
```

ASP.NET host 也可以把本地 NPS action 暴露成入站 MCP/A2A server：

```csharp
builder.Services.AddBridgeServer(options =>
{
    options.NodeId = "bridge-server-1";
    options.ServerName = "orders-bridge";
    options.PathPrefix = "/bridge-server";
    options.AddAction("orders.lookup", "Lookup an order by id.");
    options.DispatchAsync = async (frame, ct) =>
    {
        // 在这里 dispatch 到本地 Action Node / service boundary。
        return await InvokeLocalActionAsync(frame, ct);
    };
});

app.UseBridgeServer();
// POST /bridge-server/mcp  (MCP tools/list + tools/call, JSON-RPC 或 SSE)
// POST /bridge-server/a2a  (A2A tasks/send)
// GET  /bridge-server/.well-known/agent.json
```

Hosted Bridge Node 使用名为 `nps-bridge` 的 `HttpClient`；需要自定义 timeout、
proxy、TLS 或 retry policy 时，通过 `IHttpClientFactory` 配置：

```csharp
builder.Services.AddHttpClient(BridgeServiceExtensions.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

Bridge dispatcher 默认拒绝 private / loopback endpoint host（`reject_private=true`）。
只有可信本地开发目标才应显式设置 `reject_private=false`。

## Native NCP 与吊销校验

Native-mode NCP listener 可以要求在读取 `NPS/1.0\n` preamble 前先拿到
authenticated stream。用这个 hook 接入 `SslStream` 即可承载 TLS/mTLS：

```csharp
var server = new NcpServer(port, NpsFrameCodec.CreateDefault(), new NcpServerOptions
{
    RequireAuthenticatedStream = true,
    HandshakeReadTimeout = TimeSpan.FromSeconds(10),
    MaxHelloPayload = 16 * 1024,
    AuthenticateStreamAsync = async (_, stream, ct) =>
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(serverOptions, ct);
        return tls;
    }
});
```

`NipIdentVerifier` 支持静态 CRL、live callback、CA store 与 OCSP。OCSP
transport failure 默认 fail-closed；只有宿主策略明确接受风险时才设置
`OcspFailOpen=true`。

```csharp
var verifier = new NipIdentVerifier(new NipVerifierOptions
{
    TrustedIssuers = trustedIssuers,
    RevocationStore = await SqliteNipCaStore.OpenAsync("Data Source=nip-ca.db"),
    RevocationCheck = (frame, ct) => ValueTask.FromResult<NipIdentVerifyResult?>(null),
    OcspUrl = "https://ca.example.test/v1/agents"
}, httpClientFactory);
```

### 远程 NIP CA Client

```csharp
using NPS.NIP.Client;

httpClient.BaseAddress = new Uri("https://ca.example.test/");
var ca = new NipCaClient(httpClient);
var discovery = await ca.GetDiscoveryAsync(cancellationToken);
var issued = await ca.RegisterAgentAsync(new NipCaRegisterRequest(
    Identifier: "agent-1",
    PubKey: publicKeyBase64Url,
    Capabilities: ["nwp:query"],
    ScopeJson: "{}"),
    bearerToken,
    cancellationToken);
```

### Native NWP Serving

```csharp
using NPS.Core.Codecs;
using NPS.Core.Ncp;
using NPS.NWP.Native;

var nativeNode = new NwpNativeNodeServer(
    NpsFrameCodec.CreateDefault(),
    new NwpNativeNodeOptions { MemoryOptions = memoryOptions, ActionOptions = actionOptions },
    memoryProvider,
    actionProvider);

await nativeNode.ServeAsync(ncpSession, cancellationToken);
```

### Conformance Manifests

```csharp
using NPS.Conformance;

var manifest = NpsConformanceManifest.Create(
    NpsConformanceProfiles.NodeL1,
    iutName: "my-node",
    iutVersion: "0.1.0",
    iutNid: "urn:nps:node:example.test:node-1",
    peerName: "nps-dotnet-reference",
    peerVersion: "1.0.0-alpha.19",
    results: caseResults);

var validation = NpsConformanceValidator.Validate(manifest);
```

## 构建

```bash
dotnet build NPS.sln
```

## 测试

```bash
dotnet test
```

## 状态

积极开发中（v1.0.0-alpha.19）。865 个 standalone SDK 测试全部通过。

Alpha.15 主要内容：官方 `llm.complete` Action/Caps/Stream DTO contract；面向 `CapsFrame`、`StreamFrame`、异步 task result、`ErrorFrame.Details` 的 typed frame payload helper；Bridge `bridge_target` 规范 wire shape 统一到 `extras`；warning-clean .NET 包族与 SourceLink symbol；native NCP TLS hook 与有界 Hello 读取；live NIP revocation check 与 signed CRL artifact；`NipCaClient`；`NwpNativeNodeServer`；内置 HTTP/HTTPS、gRPC JSON unary、MCP JSON-RPC、A2A JSON-RPC Bridge dispatcher；transport-neutral observability renderer；`LabAcacia.NPS.Conformance`；loopback dev stack。
