English | [中文版](./README.cn.md)

# NPS .NET Reference Implementation

> First time here? Start with [START-HERE.md](START-HERE.md) for architecture, security, and review guidance.

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/badge/release-v1.0.0--alpha.18-orange.svg)](CHANGELOG.md)
[![NCP](https://img.shields.io/badge/NCP-v0.11-5b8cff.svg)]()
[![NWP](https://img.shields.io/badge/NWP-v0.21-4af0b0.svg)]()
[![NIP](https://img.shields.io/badge/NIP-v0.14-7b61ff.svg)]()
[![NDP](https://img.shields.io/badge/NDP-v0.12-f0a050.svg)]()
[![NOP](https://img.shields.io/badge/NOP-v0.9-ff8c42.svg)]()

C# / .NET 10 reference implementation for the Neural Protocol Suite.

## Alpha.18 portable profiles

The release implements the five shared protocol profiles and executes the
language-neutral fixtures under [`spec/conformance`](spec/conformance/):

- NCP 0.11 bounded native-server handshake and deterministic Caps negotiation.
- NWP 0.21 portable Node/Bridge serving and bridge lifecycle.
- NIP 0.14 portable CA, live revocation, signed CRL, and verification policy.
- NDP 0.12 signed Announce admission and registry conflict/liveness policy.
- NOP 0.9 deterministic orchestration, callback security, delegation, leases,
  and CR-0007 runtime decisions.

## NuGet Packages

| Package | Version | Description |
|---------|---------|-------------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.19 | Shared frame types (AnchorFrame, DiffFrame, StreamFrame, CapsFrame, HelloFrame, ErrorFrame), JSON/MsgPack codecs, AnchorFrame cache, frame registry |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.19 | Neural Web Protocol — NWM manifest, Query/Action/Subscribe/Diff frames, typed Action/frame payload helpers including `llm.complete`, Memory/Action/Complex/Anchor/Bridge Node middleware plus native-mode serving |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.19 | NWP Anchor Node: stateless AaaS entry point translating ActionFrames to NOP TaskFrames; `AnchorNodeClient` for `topology.snapshot` / `topology.stream` queries |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.19 | NWP Bridge Node: outbound dispatchers from NPS frames to non-NPS protocols plus inbound MCP/A2A server bridges |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.19 | Neural Identity Protocol — CA, Ed25519 key generation, IdentFrame issuance/revocation, typed remote CA client, OCSP, CRL; X.509 + ACME `agent-01` challenge (RFC-0002 prototype) |
| `LabAcacia.NPS.NIP.Storage.Sqlite` | 1.0.0-alpha.19 | SQLite storage backend for embedded/self-hosted NIP CA deployments |
| `LabAcacia.NPS.NIP.Storage.Postgres` | 1.0.0-alpha.19 | PostgreSQL storage backend for service NIP CA deployments |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.19 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.19 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |
| `LabAcacia.NPS.Daemon.Observability` | 1.0.0-alpha.19 | JSON logging, transport-neutral health/readiness renderers, ASP.NET endpoint helpers, Prometheus metrics, graceful shutdown |
| `LabAcacia.NPS.Conformance` | 1.0.0-alpha.19 | Node L1/L2 conformance case catalog, run manifest model, and CI validation helpers |

## Open vs NPS Cloud

| Area | Open package support | NPS Cloud / commercial support |
|------|----------------------|--------------------------------|
| NCP/NWP/NDP/NOP frame codecs and in-process services | Included | Managed hosting and operations |
| NIP CA, IdentFrame issuance, revocation, OCSP/CRL, X.509 prototype | Included | Managed CA operations and policy automation |
| TrustFrame parsing and basic validation | Included via `TrustFrame` and `TrustFrameValidator` for explicitly pinned grantor anchors | Hosted multi-CA federation, managed trust-anchor discovery, revocation feeds, and commercial trust-chain policy |
| Daemon observability | Included as transport-neutral renderers plus ASP.NET mapping helpers | Hosted monitoring/SLO integration |

## Quickstarts

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

`LabAcacia.NPS.Core` uses source-generated MessagePack-CSharp formatters for the built-in
Tier-2 codec. The built-in registry and upper-layer registration helpers are NativeAOT-safe.
Hosts that need a different binary codec can supply their own `IFrameCodec`
implementation by constructing `NpsFrameCodec` with custom codec instances;
the DI helper wires the default JSON + MessagePack pair.

### Dependency Injection

```csharp
using Microsoft.Extensions.DependencyInjection;
using NPS.Core.Extensions;

var services = new ServiceCollection()
    .AddNpsCore(options => options.EnableExtendedFrameHeader = true);
```

### NIP Basic TrustFrame Validation

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

### Observability Without Kestrel

```csharp
using NPS.Daemon.Observability.HealthChecks;

var health = HealthProbeRenderer.RenderHealthz();
var ready = await HealthProbeRenderer.RenderReadyzAsync(readinessProbes, cancellationToken);
```

### ASP.NET Observability Endpoints

```csharp
using NPS.Daemon.Observability;

builder.Services.AddNpsObservability();
app.MapNpsObservability();
```

### Storage Packages

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

### Bridge and Ingress Packages

`LabAcacia.NPS.NWP.Bridge` models both Bridge Node directions. `BridgeNode`
and `IBridgeDispatcher` handle outbound NPS-to-external calls with built-in
HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC dispatchers.
`McpServerBridge` and `A2aServerBridge` expose local NPS actions to external
MCP/A2A clients; inbound Bridge server dispatch requires a valid
`X-NWP-Agent` NID plus a configured `VerifyAgentAsync` hook by default, binding
the header to deployment NIP/capability policy. Request bodies and dispatches
are bounded by `MaxRequestBodyBytes` and `DispatchTimeoutMs`; set
`RequireAuth = false` only for local/dev-only ingress. The standalone
`LabAcacia.McpIngress`,
`LabAcacia.A2aIngress`, and `LabAcacia.GrpcIngress` compatibility edges are
packed by the release workflow and become consumable after the NuGet publish
step for that release.

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

ASP.NET hosts can expose the same Bridge Node over NWP endpoints:

```csharp
builder.Services.AddBridgeNode(
    options =>
    {
        options.NodeId = "bridge-1";
        options.PathPrefix = "/bridge";
    },
    dispatchers =>
    {
        // Optional: dispatchers.Register(new GrpcBridgeDispatcher(...));
    });

app.UseBridgeNode(); // GET /bridge/.nwm, GET /bridge/actions, POST /bridge/invoke
```

ASP.NET hosts can also expose local NPS actions as inbound MCP/A2A servers:

```csharp
builder.Services.AddBridgeServer(options =>
{
    options.NodeId = "bridge-server-1";
    options.ServerName = "orders-bridge";
    options.PathPrefix = "/bridge-server";
    options.AddAction("orders.lookup", "Lookup an order by id.");
    options.DispatchAsync = async (frame, ct) =>
    {
        // Dispatch to your local Action Node / service boundary here.
        return await InvokeLocalActionAsync(frame, ct);
    };
});

app.UseBridgeServer();
// POST /bridge-server/mcp  (MCP tools/list + tools/call, JSON-RPC or SSE)
// POST /bridge-server/a2a  (A2A tasks/send)
// GET  /bridge-server/.well-known/agent.json
```

The hosted Bridge Node uses the named `HttpClient` `nps-bridge`; configure it
through `IHttpClientFactory` when you need custom timeout, proxy, TLS, or retry
policy:

```csharp
builder.Services.AddHttpClient(BridgeServiceExtensions.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

Bridge dispatchers reject private and loopback endpoint hosts by default
(`reject_private=true`). Set `reject_private=false` only for trusted local
development targets.

## Native NCP and Revocation

Native-mode NCP listeners can require an authenticated stream before the
`NPS/1.0\n` preamble is read. Use the hook to install `SslStream` for TLS/mTLS:

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

`NipIdentVerifier` supports static CRLs, live callbacks, CA stores, and OCSP.
OCSP transport failures fail closed by default; set `OcspFailOpen=true` only
when the host policy explicitly accepts that risk.

```csharp
var verifier = new NipIdentVerifier(new NipVerifierOptions
{
    TrustedIssuers = trustedIssuers,
    RevocationStore = await SqliteNipCaStore.OpenAsync("Data Source=nip-ca.db"),
    RevocationCheck = (frame, ct) => ValueTask.FromResult<NipIdentVerifyResult?>(null),
    OcspUrl = "https://ca.example.test/v1/agents"
}, httpClientFactory);
```

### Remote NIP CA Client

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

## Build

```bash
dotnet build NPS.sln
```

## Test

```bash
dotnet test
```

## Status

Active development (v1.0.0-alpha.19). 865 standalone SDK tests passing.

Alpha.15 highlights: official `llm.complete` Action/Caps/Stream DTO contracts; typed frame payload helpers for `CapsFrame`, `StreamFrame`, async task results, and `ErrorFrame.Details`; Bridge `bridge_target` canonical wire shape aligned on `extras`; warning-clean .NET package family with SourceLink symbols; native NCP TLS hook and bounded Hello reads; live NIP revocation checks and signed CRL artifacts; `NipCaClient`; `NwpNativeNodeServer`; built-in Bridge dispatchers for HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC; transport-neutral observability renderers; `LabAcacia.NPS.Conformance`; loopback dev stack.