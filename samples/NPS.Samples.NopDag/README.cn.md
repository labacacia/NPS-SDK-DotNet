[English Version](./README.md) | 中文版

# NPS 示例 — NOP 三节点 DAG 演示

Phase 2 出口标准：*"NOP Orchestrator 可端到端执行一个 3 节点 DAG 任务。"*

本示例在同一进程中，于 loopback 端口上拉起**三个独立的 NWP Action Node**，
用一个包含线性 DAG 的 NOP `TaskFrame` 将它们串起来，然后让 `NopOrchestrator`
通过**真实的 HTTP** 执行该流水线，最后把 Markdown 摘要打印到 stdout。

```
┌──────────────┐      ┌────────────────┐      ┌──────────────────┐
│ content.     │ ───► │ content.       │ ───► │ content.         │
│ fetch_       │      │ summarize      │      │ publish_digest   │
│ articles     │      │                │      │                  │
│ :17441       │      │ :17442         │      │ :17443           │
└──────────────┘      └────────────────┘      └──────────────────┘
     Agent #1               Agent #2                 Agent #3
```

## 演示内容

1. 三个独立的 **NWP Action Node**（`IActionNodeProvider`），各自在自己的
   Kestrel 端口上托管一个 action。
2. 一个 **NOP TaskFrame**，DAG 含三个节点，通过 input-mapping 串联
   （`$.fetch.articles` → summarize，`$.summarize.summaries` → publish）。
3. 一个最小化的 **`INopWorkerClient` HTTP 实现**（`HttpNopWorkerClient`），
   把 DAG 中的 `agent` NID 映射到 Action Node 的 base URL，并将
   `DelegateFrame` 分派为 `POST /invoke`。
4. 端到端数据流：fetch 返回 articles 列表，summarize 对其做转换，publish
   引用上游两个节点的输出并渲染 Markdown 摘要。

## 运行

```bash
dotnet run --project impl/dotnet/samples/NPS.Samples.NopDag
```

预期输出（节选）：

```
═══ NOP 3-node DAG demo ═══
  node 1 (urn:nps:agent:demo:fetch)     → http://127.0.0.1:17441/invoke
  node 2 (urn:nps:agent:demo:summarize) → http://127.0.0.1:17442/invoke
  node 3 (urn:nps:agent:demo:publish)   → http://127.0.0.1:17443/invoke

► Submitting task 205b06a8-…
◄ Final state: Completed
  Completed 3 / 3 nodes

══════ Published digest ══════
# Daily digest — NPS protocol suite
- Why Agents need a Schema-first Protocol — NWP treats schema as a …
- Cognon Budget: a tokenizer-agnostic cost model — Counting in CGN …
- Three Node Types, one Port — Memory/Action/Complex nodes multiplex …
```

## 目录结构

```
samples/NPS.Samples.NopDag/
├── Program.cs                        # 入口：hosts + orchestrator
├── NodeHostBuilder.cs                # 为每个节点构建一个 Kestrel WebApplication
├── Providers/
│   ├── FetchArticlesProvider.cs      # 节点 1：返回硬编码文章列表
│   ├── SummarizeProvider.cs          # 节点 2：articles → summaries
│   └── PublishDigestProvider.cs      # 节点 3：summaries → Markdown 摘要
└── Orchestration/
    └── HttpNopWorkerClient.cs        # INopWorkerClient → POST /invoke
```

## 回归保护

`impl/dotnet/tests/NPS.Tests/Samples/NopDagDemoTests.cs` 使用 `TestServer`
在同进程内跑同样的流水线，保证演示在 CI 中确定性通过。

## 限制

- **仅覆盖 happy path。** Worker client 未实现完整的 NPS-5 §3.4 AlignStream
  协议——它从 CapsFrame 响应合成一个终止帧。流式、取消、preflight 均未涉及。
- **未集成 NIP。** Agent NID 是裸标识符，无证书链和能力校验。
- **线性 DAG。** 本夹具是直线三节点。菱形、K-of-N、重试、条件跳过等由
  NOP orchestrator 现有测试覆盖。

## 实验性结果引用编排

宿主可以显式选择[有界执行路径](../../src/NPS.NOP/Runtime/README.cn.md)，不是原样例默认路径。SQLite 账本
区分首次初始化与重开，历史丢失即失败。真实参考检查通过两个经过认证的数据客户端组合订单/退款查询、最小授权分析
和五行证据报告；未知效果阻断下游，取消不承诺回滚。命令、上限、所有权、不支持的路径及恢复要求见运行时说明。
