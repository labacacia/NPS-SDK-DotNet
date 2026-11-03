English | [中文版](./NPS.NOP.cn.md)

# `LabAcacia.NPS.NOP` — Class and Method Reference

> Root namespace: `NPS.NOP`
> NuGet: `LabAcacia.NPS.NOP`
> Spec: [NPS-5 NOP v0.6](https://github.com/labacacia/NPS/blob/main/NPS-5-NOP.md)

NOP is the orchestration layer. This package provides:

1. The four NOP frame types (`Task 0x40`, `Delegate 0x41`, `Sync 0x42`, `AlignStream 0x43`).
2. A DAG model and task priority / state enums.
3. The **`NopOrchestrator`** — a full lifecycle engine (preflight → DAG execution → aggregation → callback)
   with K-of-N sync support, retries with exponential backoff, condition-based skipping, and sender-NID validation.
4. Validators (`DagValidator`, `NopCallbackValidator`).
5. Helpers for condition evaluation and JSONPath-based input mapping.
6. An in-memory `INopTaskStore` plus DI extensions.

---

## Table of contents

- [Frames](#frames)
  - [`TaskFrame`](#taskframe)
  - [`DelegateFrame`](#delegateframe)
  - [`SyncFrame`](#syncframe)
  - [`AlignStreamFrame`](#alignstreamframe)
- [Task DAG model](#task-dag-model)
  - [`TaskDag` / `DagNode` / `DagEdge`](#taskdag--dagnode--dagedge)
  - [`TaskContext`](#taskcontext)
  - [`RetryPolicy` + `BackoffStrategy`](#retrypolicy--backoffstrategy)
  - [`AggregateStrategy` / `TaskPriority` / `TaskState`](#aggregatestrategy--taskpriority--taskstate)
  - [`StreamError`](#streamerror)
- [Orchestrator](#orchestrator)
  - [`INopOrchestrator`](#inoporchestrator)
  - [`NopOrchestrator`](#noporchestrator)
  - [`NopOrchestratorOptions`](#noporchestratoroptions)
  - [`INopWorkerClient` + `PreflightResult`](#inopworkerclient--preflightresult)
  - [`INopTaskStore` + `NopTaskRecord` / `NopSubtaskRecord`](#inoptaskstore--noptaskrecord--nopsubtaskrecord)
  - [`NopTaskResult`](#noptaskresult)
- [Orchestration helpers](#orchestration-helpers)
  - [`NopConditionEvaluator`](#nopconditionevaluator)
  - [`NopInputMapper`](#nopinputmapper)
  - [`NopResultAggregator`](#nopresultaggregator)
- [Validation](#validation)
  - [`DagValidator` + `DagValidationResult`](#dagvalidator--dagvalidationresult)
  - [`NopCallbackValidator`](#nopcallbackvalidator)
- [Storage](#storage)
  - [`InMemoryNopTaskStore`](#inmemorynoptaskstore)
- [Constants and error codes](#constants-and-error-codes)
- [DI extensions](#di-extensions)

---

## Frames

### `TaskFrame`

```csharp
public sealed record TaskFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Task;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    public required string       TaskId        { get; init; }   // UUID v4
    public required TaskDag      Dag           { get; init; }
    public          uint         TimeoutMs     { get; init; } = 30_000;
    public          byte         MaxRetries    { get; init; } = 2;
    public          string       Priority      { get; init; } = TaskPriority.Normal;
    public          string?      CallbackUrl   { get; init; }
    public          bool         Preflight     { get; init; }
    public          TaskContext? Context       { get; init; }
    public          string?      RequestId     { get; init; }
    public          int          DelegateDepth { get; init; }   // 0 = root
}
```

Submit to the orchestrator via `INopOrchestrator.ExecuteAsync`. `DelegateDepth` is set automatically
when the orchestrator issues sub-tasks; direct callers leave it at `0`.

### `DelegateFrame`

Orchestrator → Worker delegation.

```csharp
public sealed record DelegateFrame : IFrame
{
    public required string       ParentTaskId   { get; init; }
    public required string       SubtaskId      { get; init; }
    public required string       NodeId         { get; init; }
    public required string       TargetAgentNid { get; init; }
    public required string       Action         { get; init; }   // "nwp://..." | "preflight" | "cancel"
    public          JsonElement? Params         { get; init; }
    public required JsonElement  DelegatedScope { get; init; }   // ⊆ parent scope
    public required string       DeadlineAt     { get; init; }   // ISO 8601 UTC
    public          string?      IdempotencyKey { get; init; }
    public          string?      Priority       { get; init; }
    public          TaskContext? Context        { get; init; }
    public          int          DelegateDepth  { get; init; }
}
```

Workers MUST reject further sub-delegation when `DelegateDepth ≥ NopConstants.MaxDelegateChainDepth`.

### `SyncFrame`

K-of-N synchronisation barrier (NPS-5 §3.3).

```csharp
public sealed record SyncFrame : IFrame
{
    public required string                TaskId      { get; init; }
    public required string                SyncId      { get; init; }
    public required IReadOnlyList<string> WaitFor     { get; init; }
    public          uint                  MinRequired { get; init; }   // 0 = all
    public          string                Aggregate   { get; init; } = AggregateStrategy.Merge;
    public          uint?                 TimeoutMs   { get; init; }
}
```

### `AlignStreamFrame`

Replaces the deprecated `AlignFrame (0x05)`. Binds to a task, carries a strictly-monotonic `Seq`,
and requires the sender NID to match the expected agent (validated by the orchestrator).

```csharp
public sealed record AlignStreamFrame : IFrame
{
    public required string       StreamId    { get; init; }
    public required string       TaskId      { get; init; }
    public required string       SubtaskId   { get; init; }
    public required ulong        Seq         { get; init; }
    public          string?      PayloadRef  { get; init; }   // CapsFrame anchor ref
    public          JsonElement? Data        { get; init; }
    public          uint?        WindowSize  { get; init; }   // back-pressure in CGN tokens
    public required bool         IsFinal     { get; init; }
    public required string       SenderNid   { get; init; }
    public          StreamError? Error       { get; init; }   // populated when IsFinal && failed
}
```

---

## Task DAG model

### `TaskDag` / `DagNode` / `DagEdge`

```csharp
public sealed record TaskDag
{
    public required IReadOnlyList<DagNode> Nodes { get; init; }
    public required IReadOnlyList<DagEdge> Edges { get; init; }
}

public sealed record DagNode
{
    public required string  Id          { get; init; }
    public required string  Action      { get; init; }   // "nwp://..."
    public required string  Agent       { get; init; }   // Worker NID
    public          IReadOnlyList<string>? InputFrom { get; init; }
    public          IReadOnlyDictionary<string, JsonElement>? InputMapping { get; init; }
    public          uint?   TimeoutMs   { get; init; }
    public          RetryPolicy? RetryPolicy { get; init; }
    public          string? Condition   { get; init; }   // CEL subset
    public          uint    MinRequired { get; init; }   // K-of-N (0 = all)
}

public sealed record DagEdge(string From, string To);
```

### `TaskContext`

OpenTelemetry W3C TraceContext propagation plus custom baggage.

```csharp
public sealed record TaskContext
{
    public string?  SessionId   { get; init; }
    public string?  TraceId     { get; init; }   // 32 hex chars
    public string?  SpanId      { get; init; }   // 16 hex chars
    public byte?    TraceFlags  { get; init; }
    public IReadOnlyDictionary<string, string>? Baggage { get; init; }
    public JsonElement? Custom  { get; init; }
}
```

### `RetryPolicy` + `BackoffStrategy`

```csharp
public sealed record RetryPolicy
{
    public byte    MaxRetries     { get; init; }
    public string  Backoff        { get; init; } = BackoffStrategy.Exponential;
    public uint    InitialDelayMs { get; init; } = 1_000;
    public uint    MaxDelayMs     { get; init; } = 30_000;
    public IReadOnlyList<string>? RetryOn { get; init; }

    public uint    ComputeDelayMs(int attempt);  // min(initial * factor^attempt, max)
}

public static class BackoffStrategy
{
    public const string Fixed       = "fixed";
    public const string Linear      = "linear";
    public const string Exponential = "exponential";
}
```

`ComputeDelayMs` factor: `1` (fixed), `attempt + 1` (linear), or `2^attempt` (exponential).

### `AggregateStrategy` / `TaskPriority` / `TaskState`

```csharp
public static class AggregateStrategy
{
    public const string Merge    = "merge";      // last-write-wins object merge
    public const string First    = "first";      // first successful
    public const string All      = "all";        // array of all
    public const string FastestK = "fastest_k";  // first min_required, in order
}

public static class TaskPriority
{
    public const string Low    = "low";
    public const string Normal = "normal";
    public const string High   = "high";
}

public enum TaskState
{
    Pending, Preflight, Running, WaitingSync, Completed, Failed, Cancelled, Skipped
}
```

### `StreamError`

```csharp
public sealed record StreamError
{
    public required string Code      { get; init; }   // e.g. NOP-TASK-TIMEOUT
    public required string Message   { get; init; }
    public          bool   Retryable { get; init; }
}
```

---

## Orchestrator

### `INopOrchestrator`

```csharp
public interface INopOrchestrator
{
    Task<NopTaskResult> ExecuteAsync(TaskFrame task, CancellationToken ct = default);
    Task                CancelAsync (string taskId, CancellationToken ct = default);
    Task<NopTaskRecord?> GetStatusAsync(string taskId, CancellationToken ct = default);
}
```

### `NopOrchestrator`

Concrete engine. Constructor:

```csharp
public NopOrchestrator(
    INopWorkerClient         worker,
    INopTaskStore            store,
    NopOrchestratorOptions?  opts        = null,
    ILogger<NopOrchestrator>? log         = null,
    IHttpClientFactory?      httpFactory = null);
```

#### `ExecuteAsync` lifecycle

1. **Delegation-depth gate** — reject with `NOP-DELEGATE-CHAIN-TOO-DEEP` when
   `task.DelegateDepth ≥ NopConstants.MaxDelegateChainDepth` (3).
2. **Callback URL validation** — `NopCallbackValidator.ValidateCallbackUrl` (HTTPS-only +
   SSRF guard).
3. **DAG validation** — `DagValidator.Validate`. Topological order is cached for execution.
4. **Duplicate check** — reject if a record with the same `TaskId` already exists.
5. **Persist initial record** with `TaskState.Pending`.
6. **Create linked CTS** — bounded by `min(task.TimeoutMs, NopConstants.MaxTimeoutMs)` (1 h cap).
7. **Preflight (optional)** — dedup by agent NID and call `INopWorkerClient.PreflightAsync` in
   parallel. A single unavailable agent aborts the task with `NOP-RESOURCE-INSUFFICIENT`.
8. **DAG execution loop** — see below.
9. **Callback (optional)** — posts the `NopTaskResult` to `TaskFrame.CallbackUrl` with exponential
   backoff (3 attempts, delays 0 s / 1 s / 2 s by default).
10. **Finalisation** — persist terminal state and return the `NopTaskResult`.

#### DAG execution loop

- Track `inFlight[nodeId] → Task<NodeOutcome>`, `nodeStates[nodeId] → TaskState`, and
  `nodeResults[nodeId] → JsonElement` (completed only).
- Each iteration:
  - Identify ready nodes whose dependencies are done (considering `DagNode.MinRequired` for K-of-N).
  - Pre-emptively mark nodes as `Failed` when K can no longer be satisfied.
  - Dispatch up to `NopOrchestratorOptions.MaxConcurrentNodes` concurrently.
  - Await the next completion via `Task.WhenAny`.
  - On failure, run a reachability test (`CanReachEndNode`) and K-of-N recoverability check
    (`CanEndNodeStillSucceed`): if no end node can still succeed, abort early.
- Aggregate end-node results via `NopResultAggregator.AggregateEndNodes` using
  `NopOrchestratorOptions.DefaultAggregateStrategy` (defaults to `"merge"`).

#### Per-node execution + retry

For each DAG node:

1. Evaluate `Condition` (once, before the first attempt). A `false` result transitions the node to
   `Skipped`. A `NopConditionException` transitions it to `Failed` with `NOP-CONDITION-EVAL-ERROR`.
2. Resolve `InputMapping` via `NopInputMapper.BuildParams`.
3. Construct a `DelegateFrame` with `DelegateDepth = task.DelegateDepth + 1`, a deadline derived
   from `DagNode.TimeoutMs ?? task.TimeoutMs`, and a fresh subtask id.
4. Dispatch through `INopWorkerClient.DelegateAsync` and consume the `AlignStreamFrame` stream:
   - Enforce strict seq monotonicity — a gap returns `NOP-STREAM-SEQ-GAP`.
   - Validate `SenderNid` against `DagNode.Agent` when `NopOrchestratorOptions.ValidateSenderNid`.
   - When `IsFinal` is set: propagate any `StreamError`, otherwise capture `Data` as the node result.
5. On non-final failure, consult `RetryPolicy.RetryOn` (if present) and `MaxRetries`. Retry delays
   use `RetryPolicy.ComputeDelayMs`.
6. Persist subtask state updates via `INopTaskStore.UpdateSubtaskAsync` at each transition.

#### `CancelAsync`

Looks up the per-task `CancellationTokenSource` registered at submission time, cancels it, and
updates the store state to `TaskState.Cancelled`. In-flight subtasks receive the cancellation
signal through their linked CTS.

### `NopOrchestratorOptions`

```csharp
public sealed class NopOrchestratorOptions
{
    public int    MaxConcurrentNodes       { get; set; } = Environment.ProcessorCount * 2;
    public bool   ValidateSenderNid        { get; set; } = true;
    public bool   EnableCallback           { get; set; } = true;
    public int    CallbackTimeoutMs        { get; set; } = 10_000;
    public int    CallbackRetryBaseDelayMs { get; set; } = 1_000;   // 0 disables delay in tests
    public string DefaultAggregateStrategy { get; set; } = "merge";
}
```

### `INopWorkerClient` + `PreflightResult`

```csharp
public interface INopWorkerClient
{
    IAsyncEnumerable<AlignStreamFrame> DelegateAsync(
        DelegateFrame frame,
        CancellationToken ct = default);

    Task<PreflightResult> PreflightAsync(
        string agentNid,
        string action,
        long   estimatedNpt        = 0,
        IReadOnlyList<string>? requiredCapabilities = null,
        CancellationToken ct      = default);
}

public sealed record PreflightResult
{
    public required string AgentNid        { get; init; }
    public required bool   Available       { get; init; }
    public long?           AvailableNpt    { get; init; }
    public int?            EstimatedQueueMs{ get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public string?         UnavailableReason { get; init; }
}
```

Implement this interface once per transport (HTTP/NWP, gRPC, in-process, test double).

### `INopTaskStore` + `NopTaskRecord` / `NopSubtaskRecord`

```csharp
public interface INopTaskStore
{
    Task                SaveAsync        (NopTaskRecord record, CancellationToken ct = default);
    Task<NopTaskRecord?> GetAsync         (string taskId,         CancellationToken ct = default);
    Task                UpdateStateAsync (string taskId, TaskState state, CancellationToken ct = default);
    Task                UpdateSubtaskAsync(
        string taskId, string nodeId, string subtaskId,
        TaskState state,
        JsonElement? result    = null,
        string?      errorCode = null,
        string?      errorMsg  = null,
        int          attempt   = 1,
        CancellationToken ct   = default);
}

public sealed class NopTaskRecord
{
    public required string    TaskId    { get; init; }
    public required TaskFrame Frame     { get; init; }
    public required DateTime  StartedAt { get; init; }
    public volatile TaskState State;
    public DateTime?          CompletedAt { get; set; }
    public Dictionary<string, NopSubtaskRecord> Subtasks { get; init; } = new();
}

public sealed class NopSubtaskRecord
{
    public required string    NodeId       { get; init; }
    public required string    SubtaskId    { get; init; }
    public volatile TaskState State;
    public JsonElement?       Result       { get; set; }
    public string?            ErrorCode    { get; set; }
    public string?            ErrorMessage { get; set; }
    public int                AttemptCount { get; set; }
}
```

### `NopTaskResult`

```csharp
public sealed class NopTaskResult
{
    public required string    TaskId      { get; init; }
    public required TaskState FinalState  { get; init; }   // Completed | Failed | Cancelled
    public JsonElement?       AggregatedResult { get; init; }
    public string?            ErrorCode    { get; init; }
    public string?            ErrorMessage { get; init; }
    public IReadOnlyDictionary<string, JsonElement> NodeResults { get; init; }
        = new Dictionary<string, JsonElement>();

    public static NopTaskResult Success(string taskId, JsonElement? aggregated, IReadOnlyDictionary<string, JsonElement> nodeResults);
    public static NopTaskResult Failure(string taskId, string errorCode, string errorMessage);
    public static NopTaskResult Cancelled(string taskId, string reason);
}
```

---

## Orchestration helpers

### `NopConditionEvaluator`

```csharp
public static class NopConditionEvaluator
{
    public static bool Evaluate(
        string condition,
        IReadOnlyDictionary<string, JsonElement> context);   // nodeId → result
}

public sealed class NopConditionException : Exception { /* expression + message */ }
```

CEL-subset grammar:

| Construct    | Example                               |
|--------------|---------------------------------------|
| Comparison   | `$.classify.score > 0.7`              |
| Equality     | `$.classify.label == "spam"`          |
| Null test    | `$.classify.error != null`            |
| Boolean      | `&&`, `\|\|`, `!`                     |
| Grouping     | `(a && b) \|\| c`                     |
| Literals     | numbers, `"strings"`, `true`, `false`, `null` |
| Path access  | `$.<node_id>.<field>.<sub>`           |

Condition length is capped by `NopConstants.MaxConditionLength` (512). Any parse or resolution
failure raises `NopConditionException`, which the orchestrator maps to
`NOP-CONDITION-EVAL-ERROR` on the node.

### `NopInputMapper`

```csharp
public static class NopInputMapper
{
    public static JsonElement? Resolve(string path, IReadOnlyDictionary<string, JsonElement> context);
    public static JsonElement  BuildParams(
        IReadOnlyDictionary<string, JsonElement>? inputMapping,
        IReadOnlyDictionary<string, JsonElement>  context);
}

public sealed class NopMappingException : Exception
{
    public string ErrorCode { get; }
}
```

Path rules:

- Must start with `$.`
- `$` alone returns the full context as a JSON object
- `$.node_id` returns the node's full result
- `$.node_id.field.sub` navigates nested objects; depth capped by
  `NopConstants.MaxInputMappingDepth` (8)
- Missing properties resolve to `null` (no exception); depth overflow raises
  `NopMappingException` with code `NOP-INPUT-MAPPING-ERROR`

`BuildParams` accepts either a string path or an array of string paths per mapping key.

### `NopResultAggregator`

```csharp
public static class NopResultAggregator
{
    public static JsonElement Aggregate(
        string strategy,
        IReadOnlyList<JsonElement> results,
        int minRequired = 0);

    public static JsonElement Merge       (IReadOnlyList<JsonElement> results);
    public static JsonElement BuildArray  (IReadOnlyList<JsonElement> results);
    public static JsonElement AggregateEndNodes(
        IReadOnlyList<string>                    endNodeIds,
        IReadOnlyDictionary<string, JsonElement> allResults,
        string strategy = AggregateStrategy.Merge);
}
```

`Merge` combines object results with last-write-wins on conflicting keys; non-object results are
slotted under `"_result_{i}"`. `AggregateEndNodes` is what the orchestrator calls once the DAG
reaches a terminal state.

---

## Validation

### `DagValidator` + `DagValidationResult`

```csharp
public static class DagValidator
{
    public static DagValidationResult Validate(TaskDag dag);
}

public sealed record DagValidationResult
{
    public bool    IsValid         { get; init; }
    public string? ErrorCode       { get; init; }
    public string? ErrorMessage    { get; init; }
    public IReadOnlyList<string>? TopologicalOrder { get; init; }

    public static DagValidationResult Success(IReadOnlyList<string> order);
    public static DagValidationResult Failure(string errorCode, string message);
}
```

Checks executed, in order:

1. Non-empty node list (`NOP-TASK-DAG-INVALID`).
2. `Nodes.Count ≤ NopConstants.MaxDagNodes` (32). Violation → `NOP-TASK-DAG-TOO-LARGE`.
3. No duplicate node IDs.
4. Every edge's `From` / `To` references a known node.
5. Every node's `InputFrom` references a known node.
6. At least one start node (no incoming edges) and one end node (no outgoing edges).
7. Each `Condition` length ≤ `NopConstants.MaxConditionLength`.
8. **Kahn's algorithm** topological sort — any remaining nodes indicate a cycle (`NOP-TASK-DAG-CYCLE`).

On success the validator returns the topological order, which the orchestrator consumes directly.

### `NopCallbackValidator`

```csharp
public static class NopCallbackValidator
{
    public static string? ValidateCallbackUrl(string callbackUrl);   // null = OK
    public static bool    IsPrivateHost     (string host);
}
```

Per `NPS-5 §8.4`:

- URL MUST be `https://`.
- URL MUST NOT target localhost, `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`,
  `169.254.0.0/16`, IPv6 loopback / link-local / site-local, or `0.0.0.0/8`.
- IPv4-mapped IPv6 addresses are normalised before the check.
- No DNS resolution is performed — `IsPrivateHost` works on literal hostnames / IP strings only so
  validation stays synchronous and free of network I/O.

---

## Storage

### `InMemoryNopTaskStore`

```csharp
public sealed class InMemoryNopTaskStore : INopTaskStore { /* ConcurrentDictionary-backed */ }
```

Thread-safe, non-durable. Fine for tests and single-process deployments. `AddNopOrchestrator`
registers this by default; pass `useInMemoryStore: false` and register your own `INopTaskStore`
for durable storage.

---

## Constants and error codes

### `NopConstants`

```csharp
public static class NopConstants
{
    public const int  MaxDagNodes           = 32;
    public const int  MaxDelegateChainDepth = 3;
    public const int  MaxConditionLength    = 512;
    public const int  MaxInputMappingDepth  = 8;
    public const uint DefaultTimeoutMs      = 30_000;
    public const uint MaxTimeoutMs          = 3_600_000;   // 1 hour
    public const uint DefaultAnchorTtl      = 3_600;
    public const int  CallbackMaxRetries    = 3;           // delays 0 s, 1 s, 2 s
}
```

### `NopErrorCodes`

```csharp
public static class NopErrorCodes
{
    public const string TaskNotFound           = "NOP-TASK-NOT-FOUND";
    public const string TaskTimeout            = "NOP-TASK-TIMEOUT";
    public const string TaskDagInvalid         = "NOP-TASK-DAG-INVALID";
    public const string TaskDagCycle           = "NOP-TASK-DAG-CYCLE";
    public const string TaskDagTooLarge        = "NOP-TASK-DAG-TOO-LARGE";
    public const string TaskAlreadyCompleted   = "NOP-TASK-ALREADY-COMPLETED";
    public const string TaskCancelled          = "NOP-TASK-CANCELLED";
    public const string DelegateScopeViolation = "NOP-DELEGATE-SCOPE-VIOLATION";
    public const string DelegateRejected       = "NOP-DELEGATE-REJECTED";
    public const string DelegateChainTooDeep   = "NOP-DELEGATE-CHAIN-TOO-DEEP";
    public const string DelegateTimeout        = "NOP-DELEGATE-TIMEOUT";
    public const string SyncTimeout            = "NOP-SYNC-TIMEOUT";
    public const string SyncDependencyFailed   = "NOP-SYNC-DEPENDENCY-FAILED";
    public const string StreamSeqGap           = "NOP-STREAM-SEQ-GAP";
    public const string StreamNidMismatch      = "NOP-STREAM-NID-MISMATCH";
    public const string ResourceInsufficient   = "NOP-RESOURCE-INSUFFICIENT";
    public const string ConditionEvalError     = "NOP-CONDITION-EVAL-ERROR";
    public const string InputMappingError      = "NOP-INPUT-MAPPING-ERROR";
}
```

---

## DI extensions

```csharp
namespace NPS.NOP.Extensions;

public static class NopServiceExtensions
{
    public static IServiceCollection AddNopOrchestrator(
        this IServiceCollection services,
        Action<NopOrchestratorOptions>? configure        = null,
        bool                            useInMemoryStore = true);
}
```

Prerequisite: register an `INopWorkerClient` implementation **before** calling this method. With
`useInMemoryStore = true` (default) the orchestrator is paired with `InMemoryNopTaskStore`;
pass `false` and register a custom `INopTaskStore` (e.g. a Postgres-backed store) beforehand.

---

## End-to-end sample

```csharp
// Your transport to Worker Agents
public sealed class HttpWorkerClient(IHttpClientFactory f) : INopWorkerClient { /* … */ }

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNpsCore();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<INopWorkerClient, HttpWorkerClient>();
builder.Services.AddNopOrchestrator(opts =>
{
    opts.MaxConcurrentNodes = 8;
    opts.DefaultAggregateStrategy = AggregateStrategy.All;
});

var app          = builder.Build();
var orchestrator = app.Services.GetRequiredService<INopOrchestrator>();

var task = new TaskFrame
{
    TaskId = Guid.NewGuid().ToString("D"),
    Dag = new TaskDag
    {
        Nodes =
        [
            new DagNode { Id = "fetch",    Action = "nwp://data.example.com/articles", Agent = "urn:nps:node:data.example.com:articles" },
            new DagNode { Id = "classify", Action = "nwp://ml.example.com/classify",    Agent = "urn:nps:node:ml.example.com:classifier",
                           InputFrom = ["fetch"], InputMapping = new Dictionary<string, JsonElement>
                           {
                               ["text"] = JsonDocument.Parse("\"$.fetch.body\"").RootElement,
                           } },
            new DagNode { Id = "route",    Action = "nwp://ops.example.com/route",      Agent = "urn:nps:node:ops.example.com:router",
                           InputFrom = ["classify"],
                           Condition = "$.classify.score > 0.7" },
        ],
        Edges =
        [
            new DagEdge("fetch",    "classify"),
            new DagEdge("classify", "route"),
        ],
    },
    TimeoutMs = 60_000,
    Preflight = true,
};

var result = await orchestrator.ExecuteAsync(task);
Console.WriteLine(result.FinalState);           // Completed / Failed / Cancelled
Console.WriteLine(result.AggregatedResult);     // aggregated end-node JSON
```
