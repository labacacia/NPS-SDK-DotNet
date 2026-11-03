English | [中文版](./NPS.NWP.cn.md)

# `LabAcacia.NPS.NWP` — Class and Method Reference

> Root namespace: `NPS.NWP`
> NuGet: `LabAcacia.NPS.NWP`
> Spec: [NPS-2 NWP v0.15](https://github.com/labacacia/NPS/blob/main/NPS-2-NWP.md)

NWP is the HTTP overlay for NPS. This package provides:

1. Strongly-typed `QueryFrame` / `ActionFrame` / `AsyncActionResponse` request frames.
2. The **Memory Node middleware** — a drop-in ASP.NET Core pipeline component that exposes a
   data source as an NPS-compliant Memory Node at `/.nwm`, `/.schema`, `/query`, and `/stream`.
3. Typed Action/frame payload helpers plus the standard `llm.complete` DTO contract.
4. The Neural Web Manifest object model used by `/.nwm`.
5. HTTP header / MIME-type / error-code constants.
6. DI extensions: `AddNwp`, `AddMemoryNode<T>`, `UseMemoryNode<T>`.

---

## Table of contents

- [NWP frames](#nwp-frames)
  - [`QueryFrame`](#queryframe)
  - [`QueryOrderClause`](#queryorderclause)
  - [`VectorSearchOptions`](#vectorsearchoptions)
  - [`ActionFrame`](#actionframe)
  - [`AsyncActionResponse`](#asyncactionresponse)
- [Typed payload helpers](#typed-payload-helpers)
  - [`NwpActionPayloadCodec`](#nwpactionpayloadcodec)
  - [`NwpFramePayloadCodec`](#nwpframepayloadcodec)
  - [`llm.complete`](#llmcomplete)
- [Neural Web Manifest](#neural-web-manifest)
  - [`NeuralWebManifest`](#neuralwebmanifest)
  - [`NodeCapabilities` / `NodeAuth` / `NodeEndpoints`](#nodecapabilities--nodeauth--nodeendpoints)
  - [`NodeGraph` / `NodeGraphRef`](#nodegraph--nodegraphref)
- [HTTP surface](#http-surface)
  - [`NwpHttpHeaders`](#nwphttpheaders)
  - [MIME types](#mime-types)
  - [`NwpErrorCodes`](#nwperrorcodes)
- [Memory Node](#memory-node)
  - [`IMemoryNodeProvider` + `MemoryNodeQueryResult`](#imemorynodeprovider--memorynodequeryresult)
  - [`MemoryNodeOptions`](#memorynodeoptions)
  - [`MemoryNodeSchema` / `MemoryNodeField`](#memorynodeschema--memorynodefield)
  - [`MemoryNodeMiddleware`](#memorynodemiddleware)
- [`CognMeter` — token budget accounting](#nptmeter)
- [DI extensions](#di-extensions)

---

## NWP frames

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

Fields map 1:1 to `NPS-2 §5`. The Memory Node middleware interprets `Filter` via the
provider's `QueryAsync`; the middleware itself does **not** execute a query DSL.

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

Submit via `POST /invoke` with `Content-Type: application/nwp-frame`. If `Async = true` the server
answers with an `AsyncActionResponse`; otherwise it returns a `CapsFrame` whose first `data[]`
item contains the action-specific success payload.

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

## Typed payload helpers

### `NwpActionPayloadCodec`

`NwpActionPayloadCodec` maps typed DTOs to `ActionFrame.Params` and back:

```csharp
var frame = NwpActionPayloadCodec.ToActionFrame(
    "llm.complete",
    payload,
    new NwpActionFrameOptions { RequestId = requestId });

var payload = NwpActionPayloadCodec.ReadPayload<LlmCompleteActionRequest>(frame);
```

Helpers are available for JSON and MessagePack payloads:

```csharp
byte[] json = NwpActionPayloadCodec.EncodeJson(payload);
byte[] msgpack = NwpActionPayloadCodec.EncodeMsgPack(payload);
```

Canonical JSON and MessagePack map keys are snake_case. Decoders are
case-insensitive for JSON and tolerate PascalCase DTO property names as a
compatibility fallback.

### `NwpFramePayloadCodec`

`NwpFramePayloadCodec` maps typed DTOs to common response payload slots:

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

Use this helper when an operation's semantic contract already says what
`CapsFrame.Data[]`, `StreamFrame.Data[]`, `ActionTaskStatus.Result`, or
`ErrorFrame.Details` contains. It does not define a generic meaning for every
frame; it only avoids private JSON mapping code for typed payload slots.

### `llm.complete`

The standard LLM completion action uses `ActionFrame.ActionId = "llm.complete"`
and `ActionFrame.Params = LlmCompleteActionRequest`.

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

`LlmCompleteActionResponse` fields are `stop_reason`, `content`, `tool_calls`,
and `error`. `stop_reason` is one of `end_turn`, `tool_use`, `tool_calls`,
`max_tokens`, `length`, or `error`; tool calls use `call_id`, `tool_name`, and
`arguments_json`.

Success response modes:

| Request mode | Response |
|--------------|----------|
| `Async=false`, request payload `stream=false` | `CapsFrame.Data[0]` is `LlmCompleteActionResponse` |
| `Async=true`, request payload `stream=false` | `AsyncActionResponse`; later `system.task.status.result` is `LlmCompleteActionResponse` |
| request payload `stream=true` | `StreamFrame` sequence; `Data[]` contains `LlmCompleteStreamChunkDto` |

Protocol, validation, auth, timeout, and provider dispatch failures should use
`ErrorFrame`. `LlmCompleteActionResponse.Error` is reserved for model/provider
errors that are themselves successful action results.

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

Returned from `GET /.nwm`. This is the Agent's first read — it describes everything the Agent needs
before issuing queries.

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
    public required string Scheme        { get; init; }  // e.g. "nip"
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
    public          string? Relationship { get; init; }   // e.g. "reads", "writes"
}
```

Surfaces the node's declared upstream / downstream neighbours so Agents can traverse the graph
without hitting the Registry.

---

## HTTP surface

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

Spec: `NPS-2 §6`. Clients MUST include `X-NWP-Agent-NID` when the node's manifest advertises
`auth.required = true`.

### MIME types

| Constant                       | Value                                  |
|--------------------------------|----------------------------------------|
| `NwpMimeTypes.Frame`           | `application/nwp-frame`                |
| `NwpMimeTypes.Capsule`         | `application/nwp-capsule`              |
| `NwpMimeTypes.Manifest`        | `application/nwp-manifest+json`        |
| `NwpMimeTypes.StreamEventBytes`| `application/nwp-stream-event-bytes`   |

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

Implement once per backend (SQL Server, PostgreSQL, MongoDB, in-process store, …). The middleware
invokes `QueryAsync` for `POST /query`, `StreamAsync` for `POST /stream`, and `CountAsync` for
cursor-free total counts in responses.

Providers are resolved from DI per request via `AddMemoryNode<T>`.

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
    public string   PathPrefix         { get; set; } = "";              // e.g. "/nodes/products"
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
    public required string  Name      { get; init; }   // public / logical name
    public required string  Type      { get; init; }
    public string?          ColumnName { get; init; }  // DB column override (falls back to Name)
    public string?          Semantic  { get; init; }
    public bool             PrimaryKey { get; init; }
    public bool             Required  { get; init; }
    public bool             Nullable  { get; init; } = true;
    public JsonElement?     Default   { get; init; }
    public string?          Description { get; init; }
}
```

`ColumnName` exists so you can expose `price` in the schema while reading `unit_price_cents` from
the database without forcing projection-layer aliases.

### `MemoryNodeMiddleware`

The middleware handles four sub-paths relative to `MemoryNodeOptions.PathPrefix`:

| Path         | Verb | Purpose                                                          |
|--------------|------|------------------------------------------------------------------|
| `/.nwm`      | GET  | Returns the `NeuralWebManifest` (`application/nwp-manifest+json`) |
| `/.schema`   | GET  | Returns the `AnchorFrame` holding the node's schema              |
| `/query`     | POST | Body: `QueryFrame` — returns a `CapsFrame`                       |
| `/stream`    | POST | Body: `QueryFrame` — returns a stream of `StreamFrame`s          |

Per-request behaviour:

1. Deserialise the request body using the tier declared by `X-NWP-Tier` or defaulted from `NpsCoreOptions.DefaultTier`.
2. Enforce `MemoryNodeOptions.RequireAuth` — reject with status `NWP-AUTH-REQUIRED` when the
   `X-NWP-Agent-NID` header is missing.
3. Clamp `QueryFrame.Limit` to `MaxLimit`.
4. Track CGN consumption via `CognMeter` — trim the result list if the caller-supplied `Budget`
   would be exceeded and emit `X-NWP-Budget-Remaining` in the response.
5. Encode the response back through the shared `NpsFrameCodec`.

Errors surface as `ErrorFrame` bodies with the appropriate status code; the `NwpErrorCodes`
constant is always copied into `X-NWP-Error-Code`.

---

## `CognMeter`

```csharp
public sealed class CognMeter
{
    public CognMeter(long? budget);

    public long   Consumed        { get; }
    public long?  RemainingBudget { get; }

    public bool   TryCharge(long cost);   // false = budget exhausted
}
```

Used internally by the middleware to decide whether the next row fits under the per-request CGN
budget. Row costs follow the tokeniser-resolution chain specified by `spec/token-budget.md`.

---

## DI extensions

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

- `AddNwp` registers the NWP frame types into the `FrameRegistryBuilder` pipeline and the
  `NeuralWebManifest` factory helpers.
- `AddMemoryNode<T>` registers the provider and the options. `TProvider` is resolved as **scoped**
  (per-request); you can override the lifetime by registering it yourself before calling this method.
- `UseMemoryNode<T>` mounts `MemoryNodeMiddleware` onto the application pipeline.

---

## Putting it together

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

An Agent now sees:

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
