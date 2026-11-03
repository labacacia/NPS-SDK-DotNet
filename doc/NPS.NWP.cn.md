[English Version](./NPS.NWP.md) | 中文版

# `LabAcacia.NPS.NWP` — 类与方法参考

> 根命名空间：`NPS.NWP`
> NuGet：`LabAcacia.NPS.NWP`
> 规范：[NPS-2 NWP v0.15](https://github.com/labacacia/NPS/blob/main/NPS-2-NWP.md)

NWP 是 NPS 的 HTTP 覆盖层。本包提供：

1. 强类型 `QueryFrame` / `ActionFrame` / `AsyncActionResponse` 请求帧。
2. **Memory Node middleware** —— 一个即插即用的 ASP.NET Core 管道组件,
   将数据源暴露为符合 NPS 的 Memory Node,位于 `/.nwm`、`/.schema`、
   `/query` 和 `/stream`。
3. typed Action/frame payload helper，以及标准 `llm.complete` DTO contract。
4. `/.nwm` 使用的 Neural Web Manifest 对象模型。
5. HTTP 头 / MIME 类型 / 错误码常量。
6. DI 扩展：`AddNwp`、`AddMemoryNode<T>`、`UseMemoryNode<T>`。

---

## 目录

- [NWP 帧](#nwp-帧)
  - [`QueryFrame`](#queryframe)
  - [`QueryOrderClause`](#queryorderclause)
  - [`VectorSearchOptions`](#vectorsearchoptions)
  - [`ActionFrame`](#actionframe)
  - [`AsyncActionResponse`](#asyncactionresponse)
- [Typed payload helper](#typed-payload-helper)
  - [`NwpActionPayloadCodec`](#nwpactionpayloadcodec)
  - [`NwpFramePayloadCodec`](#nwpframepayloadcodec)
  - [`llm.complete`](#llmcomplete)
- [Neural Web Manifest](#neural-web-manifest)
  - [`NeuralWebManifest`](#neuralwebmanifest)
  - [`NodeCapabilities` / `NodeAuth` / `NodeEndpoints`](#nodecapabilities--nodeauth--nodeendpoints)
  - [`NodeGraph` / `NodeGraphRef`](#nodegraph--nodegraphref)
- [HTTP 表面](#http-表面)
  - [`NwpHttpHeaders`](#nwphttpheaders)
  - [MIME 类型](#mime-类型)
  - [`NwpErrorCodes`](#nwperrorcodes)
- [Memory Node](#memory-node)
  - [`IMemoryNodeProvider` + `MemoryNodeQueryResult`](#imemorynodeprovider--memorynodequeryresult)
  - [`MemoryNodeOptions`](#memorynodeoptions)
  - [`MemoryNodeSchema` / `MemoryNodeField`](#memorynodeschema--memorynodefield)
  - [`MemoryNodeMiddleware`](#memorynodemiddleware)
- [`CognMeter` — token budget 记账](#nptmeter)
- [DI 扩展](#di-扩展)

---

## NWP 帧

### `QueryFrame`

```csharp
public sealed record QueryFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Query;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    public required string                  AnchorRef { get; init; }
    public          IReadOnlyDictionary<string, JsonElement>? Filter { get; init; }
    public          IReadOnlyList<string>?  Projection { get; init; }
    public          IReadOnlyList<QueryOrderClause>? OrderBy   { get; init; }
    public          uint                    Limit     { get; init; } = 20;
    public          string?                 Cursor    { get; init; }
    public          VectorSearchOptions?    Vector    { get; init; }
    public          long?                   Budget    { get; init; }          // CGN tokens
}
```

字段与 `NPS-2 §5` 1:1 对应。Memory Node middleware 通过 provider 的
`QueryAsync` 解释 `Filter`；middleware 本身**不**执行查询 DSL。

### `QueryOrderClause`

```csharp
public sealed record QueryOrderClause(string Field, string Direction);
// Direction ∈ { "asc", "desc" }
```

### `VectorSearchOptions`

```csharp
public sealed record VectorSearchOptions
{
    public required string        Field    { get; init; }
    public required IReadOnlyList<float> Query { get; init; }
    public          uint          TopK     { get; init; } = 10;
    public          string?       Metric   { get; init; }   // "cosine" | "euclidean" | "dot"
}
```

### `ActionFrame`

```csharp
public sealed record ActionFrame : IFrame
{
    public required string ActionId { get; init; }
    public JsonElement? Params { get; init; }
    public string? IdempotencyKey { get; init; }
    public uint TimeoutMs { get; init; } = 5000;
    public bool Async { get; init; }
    public string? CallbackUrl { get; init; }
    public string? Priority { get; init; }
    public string? RequestId { get; init; }
}
```

使用 `Content-Type: application/nwp-frame` 通过 `POST /invoke` 提交。
若 `Async = true` 服务端以 `AsyncActionResponse` 回应；否则返回
`CapsFrame`，其首个 `data[]` 元素为 action-specific success payload。

### `AsyncActionResponse`

```csharp
public sealed record AsyncActionResponse
{
    public required string TaskId { get; init; }
    public required string Status { get; init; }
    public required string PollUrl { get; init; }
    public uint? EstimatedMs { get; init; }
    public string? RequestId { get; init; }
}
```

---

## Typed payload helper

### `NwpActionPayloadCodec`

`NwpActionPayloadCodec` 将 typed DTO 映射到 `ActionFrame.Params`，也可从
`ActionFrame.Params` 读回 DTO：

```csharp
var frame = NwpActionPayloadCodec.ToActionFrame(
    "llm.complete",
    payload,
    new NwpActionFrameOptions { RequestId = requestId });

var payload = NwpActionPayloadCodec.ReadPayload<LlmCompleteActionRequest>(frame);
```

同时提供 JSON 与 MessagePack payload helper：

```csharp
byte[] json = NwpActionPayloadCodec.EncodeJson(payload);
byte[] msgpack = NwpActionPayloadCodec.EncodeMsgPack(payload);
```

规范 JSON 与 MessagePack map key 均为 snake_case。JSON decoder 对大小写不敏感，
并兼容 PascalCase DTO 属性名，作为迁移期 fallback。

### `NwpFramePayloadCodec`

`NwpFramePayloadCodec` 将 typed DTO 映射到常见响应 payload 槽位：

```csharp
var caps = NwpFramePayloadCodec.ToCapsFrame(anchorRef, result);
var result = NwpFramePayloadCodec.ReadCapsPayload<MyResultDto>(caps);

var stream = NwpFramePayloadCodec.ToStreamFrame(
    streamId,
    seq: 0,
    isLast: false,
    payloads: chunks,
    anchorRef: anchorRef);

var chunks = NwpFramePayloadCodec.ReadStreamPayloads<MyChunkDto>(stream);

var taskResult = NwpFramePayloadCodec.ReadTaskResult<MyResultDto>(taskStatus);
var errorDetails = NwpFramePayloadCodec.ReadErrorDetails<MyErrorDetailsDto>(errorFrame);
```

当某个 operation 的语义 contract 已经说明 `CapsFrame.Data[]` 或
`StreamFrame.Data[]`、`ActionTaskStatus.Result`、`ErrorFrame.Details` 里是什么
DTO 时，使用这个 helper。它不会给所有 frame 强行定义统一语义，只是避免
typed payload 槽位继续散落私有 JSON mapping。

### `llm.complete`

标准 LLM completion action 使用 `ActionFrame.ActionId = "llm.complete"`，
且 `ActionFrame.Params = LlmCompleteActionRequest`。

```csharp
var frame = LlmCompleteAction.ToActionFrame(new LlmCompleteActionRequest
{
    Model = "llama3.1:8b",
    MaxTokens = 4096,
    Stream = false,
    Messages =
    [
        new LlmMessageDto { Role = "system", Content = "Answer tersely." },
        new LlmMessageDto { Role = "user", Content = "Explain NPS anchors." },
    ],
    Tools =
    [
        new LlmToolDefinitionDto
        {
            Name = "memory.search",
            Description = "Search agent memory.",
            Parameters =
            [
                new ToolParameterDto
                {
                    Name = "query",
                    Type = "string",
                    Required = true,
                },
            ],
        },
    ],
});

var request = LlmCompleteAction.ReadRequest(frame);

var syncResponse = LlmCompleteAction.ToCapsFrame(new LlmCompleteActionResponse
{
    StopReason = LlmStopReason.EndTurn,
    Content = "Anchors let a client send a schema once, then reference it.",
});
var result = LlmCompleteAction.ReadResponse(syncResponse);

var asyncResult = LlmCompleteAction.ReadAsyncResult(taskStatus);

var streamFrame = LlmCompleteAction.ToStreamFrame(
    "stream-1",
    seq: 0,
    isLast: false,
    [new LlmCompleteStreamChunkDto { ContentDelta = "Anchors " }],
    includeAnchorRef: true);
var streamChunks = LlmCompleteAction.ReadStreamChunks(streamFrame);
```

`LlmCompleteActionResponse` 字段为 `stop_reason`、`content`、`tool_calls`
和 `error`。`stop_reason` 取值为 `end_turn`、`tool_use`、`tool_calls`、
`max_tokens`、`length`、`error`；tool call 使用 `call_id`、`tool_name`、
`arguments_json`。

成功响应模式：

| 请求模式 | 响应 |
|---------|------|
| `Async=false`，请求 payload `stream=false` | `CapsFrame.Data[0]` 为 `LlmCompleteActionResponse` |
| `Async=true`，请求 payload `stream=false` | `AsyncActionResponse`；之后 `system.task.status.result` 为 `LlmCompleteActionResponse` |
| 请求 payload `stream=true` | `StreamFrame` 序列；`Data[]` 为 `LlmCompleteStreamChunkDto` |

协议、校验、鉴权、超时和 provider dispatch 失败应使用 `ErrorFrame`。
`LlmCompleteActionResponse.Error` 仅保留给“模型/提供商层 error 本身是一次
成功 action result”的情况。

---

## Neural Web Manifest

### `NeuralWebManifest`

```csharp
public sealed record NeuralWebManifest
{
    public required string            NodeId      { get; init; }
    public required string            DisplayName { get; init; }
    public required string            NodeType    { get; init; }   // "memory" | "action" | "complex" | "gateway"
    public required NodeCapabilities  Capabilities { get; init; }
    public required NodeAuth          Auth        { get; init; }
    public required NodeEndpoints     Endpoints   { get; init; }
    public          NodeGraph?        Graph       { get; init; }
    public          IReadOnlyList<string>? Tags   { get; init; }
    public          JsonElement?      Extensions  { get; init; }
}
```

由 `GET /.nwm` 返回。这是 Agent 的第一次读取 —— 在发起查询之前,
它描述 Agent 需要的一切。

### `NodeCapabilities` / `NodeAuth` / `NodeEndpoints`

```csharp
public sealed record NodeCapabilities
{
    public bool Query    { get; init; }
    public bool Stream   { get; init; }
    public bool Invoke   { get; init; }
    public bool Vector   { get; init; }
    public IReadOnlyList<string>? Tiers { get; init; }   // "json","msgpack"
}

public sealed record NodeAuth
{
    public required string Scheme        { get; init; }  // 如 "nip"
    public          bool   Required      { get; init; }
    public          IReadOnlyList<string>? RequiredCapabilities { get; init; }
}

public sealed record NodeEndpoints
{
    public string? Schema  { get; init; }   // "/.schema"
    public string? Query   { get; init; }   // "/query"
    public string? Stream  { get; init; }   // "/stream"
    public string? Invoke  { get; init; }   // "/invoke"
    public string? Status  { get; init; }   // "/status/{actionId}"
}
```

### `NodeGraph` / `NodeGraphRef`

```csharp
public sealed record NodeGraph
{
    public IReadOnlyList<NodeGraphRef>? Upstream   { get; init; }
    public IReadOnlyList<NodeGraphRef>? Downstream { get; init; }
}

public sealed record NodeGraphRef
{
    public required string Nid          { get; init; }
    public          string? Relationship { get; init; }   // 如 "reads"、"writes"
}
```

暴露节点声明的上游 / 下游邻居,使 Agent 无需访问 Registry 即可遍历图谱。

---

## HTTP 表面

### `NwpHttpHeaders`

```csharp
public static class NwpHttpHeaders
{
    public const string Depth          = "X-NWP-Depth";
    public const string AgentNid       = "X-NWP-Agent-NID";
    public const string TraceId        = "X-NWP-Trace-ID";
    public const string SpanId         = "X-NWP-Span-ID";
    public const string Budget         = "X-NWP-Budget";
    public const string BudgetRemaining = "X-NWP-Budget-Remaining";
    public const string ErrorCode      = "X-NWP-Error-Code";
    public const string NextCursor     = "X-NWP-Next-Cursor";
    public const string Tier           = "X-NWP-Tier";
}
```

规范：`NPS-2 §6`。当节点 manifest 声明 `auth.required = true` 时,
客户端**必须**附带 `X-NWP-Agent-NID`。

### MIME 类型

| 常量                            | 值                                     |
|---------------------------------|----------------------------------------|
| `NwpMimeTypes.Frame`            | `application/nwp-frame`                |
| `NwpMimeTypes.Capsule`          | `application/nwp-capsule`              |
| `NwpMimeTypes.Manifest`         | `application/nwp-manifest+json`        |
| `NwpMimeTypes.StreamEventBytes` | `application/nwp-stream-event-bytes`   |

### `NwpErrorCodes`

```csharp
public static class NwpErrorCodes
{
    public const string AnchorNotFound     = "NWP-ANCHOR-NOT-FOUND";
    public const string QueryInvalid       = "NWP-QUERY-INVALID";
    public const string ProjectionInvalid  = "NWP-PROJECTION-INVALID";
    public const string FilterInvalid      = "NWP-FILTER-INVALID";
    public const string LimitExceeded      = "NWP-LIMIT-EXCEEDED";
    public const string BudgetExceeded     = "NWP-BUDGET-EXCEEDED";
    public const string VectorUnsupported  = "NWP-VECTOR-UNSUPPORTED";
    public const string ActionUnknown      = "NWP-ACTION-UNKNOWN";
    public const string ActionTimeout      = "NWP-ACTION-TIMEOUT";
    public const string NodeUnavailable    = "NWP-NODE-UNAVAILABLE";
    public const string AuthRequired       = "NWP-AUTH-REQUIRED";
}
```

---

## Memory Node

### `IMemoryNodeProvider` + `MemoryNodeQueryResult`

```csharp
public interface IMemoryNodeProvider
{
    Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame frame,
        MemoryNodeQueryContext ctx,
        CancellationToken ct);

    IAsyncEnumerable<StreamFrame> StreamAsync(
        QueryFrame frame,
        MemoryNodeQueryContext ctx,
        CancellationToken ct);

    Task<long> CountAsync(
        QueryFrame frame,
        MemoryNodeQueryContext ctx,
        CancellationToken ct);
}

public sealed record MemoryNodeQueryResult(
    IReadOnlyList<JsonElement> Rows,
    string?                    NextCursor,
    long?                      TotalCount);

public sealed record MemoryNodeQueryContext(
    string? AgentNid,
    long?   RemainingBudget,
    string? TraceId);
```

每种后端（SQL Server、PostgreSQL、MongoDB、进程内存储……）实现一次。
middleware 为 `POST /query` 调用 `QueryAsync`,为 `POST /stream` 调用
`StreamAsync`,为响应中的无游标总数调用 `CountAsync`。

Provider 通过 `AddMemoryNode<T>` 按请求从 DI 解析。

### `MemoryNodeOptions`

```csharp
public sealed class MemoryNodeOptions
{
    public required string             NodeId        { get; set; }      // "urn:nps:node:..."
    public required string             DisplayName   { get; set; }
    public required MemoryNodeSchema   Schema        { get; set; }

    public int      DefaultLimit       { get; set; } = 20;
    public int      MaxLimit           { get; set; } = 1000;
    public bool     RequireAuth        { get; set; } = false;
    public long?    DefaultTokenBudget { get; set; }
    public string   PathPrefix         { get; set; } = "";              // 如 "/nodes/products"
    public IReadOnlyList<string>? AdditionalCapabilities { get; set; }
}
```

### `MemoryNodeSchema` / `MemoryNodeField`

```csharp
public sealed record MemoryNodeSchema
{
    public required IReadOnlyList<MemoryNodeField> Fields { get; init; }
    public string? Family { get; init; }
}

public sealed record MemoryNodeField
{
    public required string  Name      { get; init; }   // 公开 / 逻辑名
    public required string  Type      { get; init; }
    public string?          ColumnName { get; init; }  // DB 列覆写（缺省回退到 Name）
    public string?          Semantic  { get; init; }
    public bool             PrimaryKey { get; init; }
    public bool             Required  { get; init; }
    public bool             Nullable  { get; init; } = true;
    public JsonElement?     Default   { get; init; }
    public string?          Description { get; init; }
}
```

`ColumnName` 存在的意义是:你可以在 schema 里暴露 `price`,同时从数据库读取
`unit_price_cents`,无需在 projection 层强加别名。

### `MemoryNodeMiddleware`

Middleware 相对 `MemoryNodeOptions.PathPrefix` 处理四个子路径：

| 路径         | 动词 | 用途                                                             |
|--------------|------|------------------------------------------------------------------|
| `/.nwm`      | GET  | 返回 `NeuralWebManifest`（`application/nwp-manifest+json`）      |
| `/.schema`   | GET  | 返回持有节点 schema 的 `AnchorFrame`                             |
| `/query`     | POST | 请求体：`QueryFrame` —— 返回 `CapsFrame`                         |
| `/stream`    | POST | 请求体：`QueryFrame` —— 返回 `StreamFrame` 流                    |

每请求行为：

1. 按 `X-NWP-Tier` 声明或从 `NpsCoreOptions.DefaultTier` 缺省的 tier 反序列化请求体。
2. 强制 `MemoryNodeOptions.RequireAuth` —— `X-NWP-Agent-NID` 头缺失时以
   `NWP-AUTH-REQUIRED` 状态拒绝。
3. 将 `QueryFrame.Limit` 钳制到 `MaxLimit`。
4. 经 `CognMeter` 追踪 CGN 消耗 —— 若将超过调用者提供的 `Budget` 则修剪
   结果列表,并在响应中发出 `X-NWP-Budget-Remaining`。
5. 通过共享的 `NpsFrameCodec` 编码响应。

错误以 `ErrorFrame` 作为响应体并带相应状态码；`NwpErrorCodes` 常量总是
被拷贝到 `X-NWP-Error-Code`。

---

## `CognMeter`

```csharp
public sealed class CognMeter
{
    public CognMeter(long? budget);

    public long   Consumed        { get; }
    public long?  RemainingBudget { get; }

    public bool   TryCharge(long cost);   // false = 预算耗尽
}
```

middleware 内部使用,用于判断下一行是否仍在每请求 CGN 预算内。
行成本遵循 `spec/token-budget.md` 指定的 tokenizer 解析链。

---

## DI 扩展

```csharp
namespace NPS.NWP.Extensions;

public static class NwpServiceExtensions
{
    public static IServiceCollection AddNwp(this IServiceCollection services);

    public static IServiceCollection AddMemoryNode<TProvider>(
        this IServiceCollection  services,
        Action<MemoryNodeOptions> configure)
        where TProvider : class, IMemoryNodeProvider;

    public static IApplicationBuilder UseMemoryNode<TProvider>(
        this IApplicationBuilder app)
        where TProvider : class, IMemoryNodeProvider;
}
```

- `AddNwp` 将 NWP 帧类型注册到 `FrameRegistryBuilder` 管道以及
  `NeuralWebManifest` 工厂助手。
- `AddMemoryNode<T>` 注册 provider 与 options。`TProvider` 以**Scoped**
  （每请求）解析；在调用此方法之前你可以自行注册以覆盖生命周期。
- `UseMemoryNode<T>` 将 `MemoryNodeMiddleware` 挂载到应用管道。

---

## 整合示例

```csharp
public sealed class ProductsProvider(ProductsDbContext db) : IMemoryNodeProvider { /* ... */ }

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNpsCore();
builder.Services.AddNwp();
builder.Services.AddDbContext<ProductsDbContext>();
builder.Services.AddMemoryNode<ProductsProvider>(o =>
{
    o.NodeId      = "urn:nps:node:api.example.com:products";
    o.DisplayName = "Products catalogue";
    o.Schema      = new MemoryNodeSchema
    {
        Fields =
        [
            new MemoryNodeField { Name = "id",    Type = "uint64",  PrimaryKey = true },
            new MemoryNodeField { Name = "price", Type = "decimal", Semantic   = "commerce.price.usd" },
            new MemoryNodeField { Name = "title", Type = "string",  Required   = true },
        ],
    };
    o.DefaultLimit = 50;
    o.RequireAuth  = true;
});

var app = builder.Build();
app.UseRouting();
app.UseMemoryNode<ProductsProvider>();
app.Run();
```

此时 Agent 看到：

```http
GET /.nwm
→ 200 application/nwp-manifest+json   (NeuralWebManifest)

GET /.schema
→ 200 application/nwp-frame           (AnchorFrame)

POST /query      Content-Type: application/nwp-frame   Body: QueryFrame
→ 200 application/nwp-frame           (CapsFrame)

POST /stream     Content-Type: application/nwp-frame   Body: QueryFrame
→ 200 application/nwp-stream-event-bytes (StreamFrame*)
```
