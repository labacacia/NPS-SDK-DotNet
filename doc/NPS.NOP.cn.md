[English Version](./NPS.NOP.md) | 中文版

# `LabAcacia.NPS.NOP` — 类与方法参考

> 根命名空间：`NPS.NOP`
> NuGet：`LabAcacia.NPS.NOP`
> 规范：[NPS-5 NOP v0.6](https://github.com/labacacia/NPS/blob/main/NPS-5-NOP.md)

NOP 是编排层。本包提供：

1. 四种 NOP 帧类型（`Task 0x40`、`Delegate 0x41`、`Sync 0x42`、`AlignStream 0x43`）。
2. DAG 模型以及 task 优先级 / 状态枚举。
3. **`NopOrchestrator`** —— 完整生命周期引擎（preflight → DAG 执行 → 聚合 → 回调）,
   支持 K-of-N 同步、指数退避重试、基于条件的跳过与发送者 NID 校验。
4. 验证器（`DagValidator`、`NopCallbackValidator`）。
5. 条件求值与基于 JSONPath 的输入映射助手。
6. 内存 `INopTaskStore` 以及 DI 扩展。

---

## 目录

- [帧](#帧)
  - [`TaskFrame`](#taskframe)
  - [`DelegateFrame`](#delegateframe)
  - [`SyncFrame`](#syncframe)
  - [`AlignStreamFrame`](#alignstreamframe)
- [Task DAG 模型](#task-dag-模型)
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
- [编排助手](#编排助手)
  - [`NopConditionEvaluator`](#nopconditionevaluator)
  - [`NopInputMapper`](#nopinputmapper)
  - [`NopResultAggregator`](#nopresultaggregator)
- [校验](#校验)
  - [`DagValidator` + `DagValidationResult`](#dagvalidator--dagvalidationresult)
  - [`NopCallbackValidator`](#nopcallbackvalidator)
- [存储](#存储)
  - [`InMemoryNopTaskStore`](#inmemorynoptaskstore)
- [常量与错误码](#常量与错误码)
- [DI 扩展](#di-扩展)

---

## 帧

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
    public          int          DelegateDepth { get; init; }   // 0 = 根任务
}
```

通过 `INopOrchestrator.ExecuteAsync` 提交给 orchestrator。
`DelegateDepth` 在 orchestrator 派发子任务时自动设置；直接调用者将其保持为 `0`。

### `DelegateFrame`

Orchestrator → Worker 委托。

```csharp
public sealed record DelegateFrame : IFrame
{
    public required string       ParentTaskId   { get; init; }
    public required string       SubtaskId      { get; init; }
    public required string       NodeId         { get; init; }
    public required string       TargetAgentNid { get; init; }
    public required string       Action         { get; init; }   // "nwp://..." | "preflight" | "cancel"
    public          JsonElement? Params         { get; init; }
    public required JsonElement  DelegatedScope { get; init; }   // ⊆ 父 scope
    public required string       DeadlineAt     { get; init; }   // ISO 8601 UTC
    public          string?      IdempotencyKey { get; init; }
    public          string?      Priority       { get; init; }
    public          TaskContext? Context        { get; init; }
    public          int          DelegateDepth  { get; init; }
}
```

当 `DelegateDepth ≥ NopConstants.MaxDelegateChainDepth` 时,worker **必须**
拒绝进一步的子委托。

### `SyncFrame`

K-of-N 同步屏障（NPS-5 §3.3）。

```csharp
public sealed record SyncFrame : IFrame
{
    public required string                TaskId      { get; init; }
    public required string                SyncId      { get; init; }
    public required IReadOnlyList<string> WaitFor     { get; init; }
    public          uint                  MinRequired { get; init; }   // 0 = 全部
    public          string                Aggregate   { get; init; } = AggregateStrategy.Merge;
    public          uint?                 TimeoutMs   { get; init; }
}
```

### `AlignStreamFrame`

替代已弃用的 `AlignFrame (0x05)`。绑定到 task,携带严格单调 `Seq`,并要求
发送者 NID 与期望的 agent 匹配（由 orchestrator 校验）。

```csharp
public sealed record AlignStreamFrame : IFrame
{
    public required string       StreamId    { get; init; }
    public required string       TaskId      { get; init; }
    public required string       SubtaskId   { get; init; }
    public required ulong        Seq         { get; init; }
    public          string?      PayloadRef  { get; init; }   // CapsFrame anchor ref
    public          JsonElement? Data        { get; init; }
    public          uint?        WindowSize  { get; init; }   // CGN token 反压
    public required bool         IsFinal     { get; init; }
    public required string       SenderNid   { get; init; }
    public          StreamError? Error       { get; init; }   // IsFinal && 失败时填充
}
```

---

## Task DAG 模型

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
    public          string? Condition   { get; init; }   // CEL 子集
    public          uint    MinRequired { get; init; }   // K-of-N（0 = 全部）
}

public sealed record DagEdge(string From, string To);
```

### `TaskContext`

OpenTelemetry W3C TraceContext 传播加自定义 baggage。

```csharp
public sealed record TaskContext
{
    public string?  SessionId   { get; init; }
    public string?  TraceId     { get; init; }   // 32 hex 字符
    public string?  SpanId      { get; init; }   // 16 hex 字符
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

`ComputeDelayMs` 因子：`1`（fixed）、`attempt + 1`（linear）、或 `2^attempt`（exponential）。

### `AggregateStrategy` / `TaskPriority` / `TaskState`

```csharp
public static class AggregateStrategy
{
    public const string Merge    = "merge";      // 后写覆盖的对象 merge
    public const string First    = "first";      // 第一个成功
    public const string All      = "all";        // 全部数组
    public const string FastestK = "fastest_k";  // 前 min_required 个（按顺序）
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
    public required string Code      { get; init; }   // 如 NOP-TASK-TIMEOUT
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

具体引擎。构造函数：

```csharp
public NopOrchestrator(
    INopWorkerClient         worker,
    INopTaskStore            store,
    NopOrchestratorOptions?  opts        = null,
    ILogger<NopOrchestrator>? log         = null,
    IHttpClientFactory?      httpFactory = null);
```

#### `ExecuteAsync` 生命周期

1. **委托深度闸门** —— 当 `task.DelegateDepth ≥ NopConstants.MaxDelegateChainDepth`(3)
   时以 `NOP-DELEGATE-CHAIN-TOO-DEEP` 拒绝。
2. **回调 URL 校验** —— `NopCallbackValidator.ValidateCallbackUrl`（仅 HTTPS + SSRF 防护）。
3. **DAG 校验** —— `DagValidator.Validate`。拓扑序缓存供执行使用。
4. **重复检查** —— 若相同 `TaskId` 的记录已存在则拒绝。
5. **持久化初始记录**,状态为 `TaskState.Pending`。
6. **创建链式 CTS** —— 以 `min(task.TimeoutMs, NopConstants.MaxTimeoutMs)`（1 小时上限）为边界。
7. **Preflight（可选）** —— 按 agent NID 去重并并行调用
   `INopWorkerClient.PreflightAsync`。任一 agent 不可用即以
   `NOP-RESOURCE-INSUFFICIENT` 中止任务。
8. **DAG 执行循环** —— 见下。
9. **回调（可选）** —— 以指数退避（默认 3 次尝试,延迟 0s / 1s / 2s）将
   `NopTaskResult` POST 到 `TaskFrame.CallbackUrl`。
10. **最终化** —— 持久化终态并返回 `NopTaskResult`。

#### DAG 执行循环

- 追踪 `inFlight[nodeId] → Task<NodeOutcome>`、`nodeStates[nodeId] → TaskState`、
  `nodeResults[nodeId] → JsonElement`（仅完成时）。
- 每次迭代：
  - 识别依赖已完成的就绪节点（考虑 `DagNode.MinRequired` 的 K-of-N）。
  - 当 K 已不可能满足时抢先将节点标记为 `Failed`。
  - 并发派发,最多 `NopOrchestratorOptions.MaxConcurrentNodes` 个。
  - 经 `Task.WhenAny` 等待下一次完成。
  - 失败时运行可达性检测（`CanReachEndNode`）与 K-of-N 可恢复性检测
    （`CanEndNodeStillSucceed`）：若无 end node 仍可能成功,提前中止。
- 通过 `NopResultAggregator.AggregateEndNodes` 使用
  `NopOrchestratorOptions.DefaultAggregateStrategy`（默认 `"merge"`）聚合 end-node 结果。

#### 逐节点执行 + 重试

对每个 DAG 节点：

1. 求值 `Condition`（一次,首次尝试之前）。`false` 使节点转为 `Skipped`。
   `NopConditionException` 以 `NOP-CONDITION-EVAL-ERROR` 使其转为 `Failed`。
2. 经 `NopInputMapper.BuildParams` 解析 `InputMapping`。
3. 构造 `DelegateFrame`,设 `DelegateDepth = task.DelegateDepth + 1`、
   从 `DagNode.TimeoutMs ?? task.TimeoutMs` 派生的截止时间、一个新鲜的 subtask id。
4. 经 `INopWorkerClient.DelegateAsync` 派发并消费 `AlignStreamFrame` 流：
   - 强制严格 seq 单调 —— 出现跳变返回 `NOP-STREAM-SEQ-GAP`。
   - 当 `NopOrchestratorOptions.ValidateSenderNid` 时对 `DagNode.Agent`
     校验 `SenderNid`。
   - 当 `IsFinal` 置位时：传播任何 `StreamError`,否则将 `Data` 捕获为节点结果。
5. 非 final 失败时,参考 `RetryPolicy.RetryOn`（若存在）与 `MaxRetries`。
   重试延迟使用 `RetryPolicy.ComputeDelayMs`。
6. 经 `INopTaskStore.UpdateSubtaskAsync` 在每次状态转换处持久化子任务状态更新。

#### `CancelAsync`

查找在提交时注册的逐任务 `CancellationTokenSource`,将其取消,并将 store
状态更新为 `TaskState.Cancelled`。进行中的子任务经其链式 CTS 收到取消信号。

### `NopOrchestratorOptions`

```csharp
public sealed class NopOrchestratorOptions
{
    public int    MaxConcurrentNodes       { get; set; } = Environment.ProcessorCount * 2;
    public bool   ValidateSenderNid        { get; set; } = true;
    public bool   EnableCallback           { get; set; } = true;
    public int    CallbackTimeoutMs        { get; set; } = 10_000;
    public int    CallbackRetryBaseDelayMs { get; set; } = 1_000;   // 0 在测试中禁用延迟
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

每种传输（HTTP/NWP、gRPC、进程内、测试替身）实现此接口一次。

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

## 编排助手

### `NopConditionEvaluator`

```csharp
public static class NopConditionEvaluator
{
    public static bool Evaluate(
        string condition,
        IReadOnlyDictionary<string, JsonElement> context);   // nodeId → result
}

public sealed class NopConditionException : Exception { /* 表达式 + 消息 */ }
```

CEL 子集文法：

| 构造          | 示例                                    |
|---------------|-----------------------------------------|
| 比较          | `$.classify.score > 0.7`                |
| 相等          | `$.classify.label == "spam"`            |
| 空值测试      | `$.classify.error != null`              |
| 布尔          | `&&`、`\|\|`、`!`                        |
| 分组          | `(a && b) \|\| c`                        |
| 字面量        | 数字、`"strings"`、`true`、`false`、`null` |
| 路径访问      | `$.<node_id>.<field>.<sub>`             |

条件长度由 `NopConstants.MaxConditionLength`（512）限制。任何解析或解析失败
抛出 `NopConditionException`,orchestrator 将其映射为节点上的
`NOP-CONDITION-EVAL-ERROR`。

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

路径规则：

- 必须以 `$.` 起头
- 仅 `$` 返回完整上下文作为 JSON 对象
- `$.node_id` 返回节点的完整结果
- `$.node_id.field.sub` 导航嵌套对象；深度由
  `NopConstants.MaxInputMappingDepth`（8）限制
- 缺失属性解析为 `null`（不抛异常）；深度溢出抛
  `NopMappingException` 并使用错误码 `NOP-INPUT-MAPPING-ERROR`

`BuildParams` 每个映射键既接受字符串路径,也接受字符串路径数组。

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

`Merge` 以后写覆盖合并对象结果;非对象结果放到 `"_result_{i}"` 槽。
当 DAG 达到终态时,orchestrator 会调用 `AggregateEndNodes`。

---

## 校验

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

按顺序执行的检查：

1. 节点列表非空（`NOP-TASK-DAG-INVALID`）。
2. `Nodes.Count ≤ NopConstants.MaxDagNodes`（32）。违反 → `NOP-TASK-DAG-TOO-LARGE`。
3. 无重复节点 ID。
4. 每条边的 `From` / `To` 引用已知节点。
5. 每个节点的 `InputFrom` 引用已知节点。
6. 至少一个 start 节点（无入边）和一个 end 节点（无出边）。
7. 每个 `Condition` 长度 ≤ `NopConstants.MaxConditionLength`。
8. **Kahn 算法**拓扑排序 —— 任何剩余节点说明存在环（`NOP-TASK-DAG-CYCLE`）。

成功时校验器返回拓扑序,orchestrator 直接消费。

### `NopCallbackValidator`

```csharp
public static class NopCallbackValidator
{
    public static string? ValidateCallbackUrl(string callbackUrl);   // null = OK
    public static bool    IsPrivateHost     (string host);
}
```

按 `NPS-5 §8.4`：

- URL 必须为 `https://`。
- URL 不得指向 localhost、`127.0.0.0/8`、`10.0.0.0/8`、`172.16.0.0/12`、
  `192.168.0.0/16`、`169.254.0.0/16`、IPv6 环回 / 链路本地 / 站点本地、
  或 `0.0.0.0/8`。
- IPv4 映射的 IPv6 地址在检查前被规范化。
- 不执行 DNS 解析 —— `IsPrivateHost` 仅作用于字面主机名 / IP 字符串,
  使校验保持同步且无网络 I/O。

---

## 存储

### `InMemoryNopTaskStore`

```csharp
public sealed class InMemoryNopTaskStore : INopTaskStore { /* ConcurrentDictionary 支持 */ }
```

线程安全、非持久。适合测试与单进程部署。`AddNopOrchestrator` 默认注册此实现；
传 `useInMemoryStore: false` 并注册你自己的 `INopTaskStore` 以获得持久存储。

---

## 常量与错误码

### `NopConstants`

```csharp
public static class NopConstants
{
    public const int  MaxDagNodes           = 32;
    public const int  MaxDelegateChainDepth = 3;
    public const int  MaxConditionLength    = 512;
    public const int  MaxInputMappingDepth  = 8;
    public const uint DefaultTimeoutMs      = 30_000;
    public const uint MaxTimeoutMs          = 3_600_000;   // 1 小时
    public const uint DefaultAnchorTtl      = 3_600;
    public const int  CallbackMaxRetries    = 3;           // 延迟 0s、1s、2s
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

## DI 扩展

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

前置条件：调用此方法**之前**先注册一个 `INopWorkerClient` 实现。
当 `useInMemoryStore = true`（默认）时,orchestrator 与 `InMemoryNopTaskStore`
配对；传 `false` 并事先注册自定义 `INopTaskStore`（例如 Postgres 支持的 store）。

---

## 端到端示例

```csharp
// 你到 Worker Agent 的传输
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
Console.WriteLine(result.AggregatedResult);     // 聚合的 end-node JSON
```
