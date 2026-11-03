[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— .NET SDK

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

在 NPS 达到 v1.0 稳定版之前，套件内所有仓库同步使用同一个预发布版本号。

---

## [1.0.0-alpha.20] — 发布候选，尚未发布

- 有边界实验：结果资源/授权、重复schema批次、本地任务交接和易失节点转移；各语言支持范围不同，未承诺六SDK完整运行时一致性。
- 保留与等价HTTPgzip及普通工具的真实测量；不宣称固定token、费用或速度收益。
- 候选使用统一版本、本地CI与独立包验证；完整生产能力、beta和实际发布保持后续门槛。

## [Unreleased]

- 将 MessagePack 升至 3.1.9、SQLite 原生 bundle 升至 3.0.5，保留固定 wire 向量并对齐源码/发布 overlay。

- 新增显式选择的易失结果发现，绑定当前宿主所有权策略、新鲜度/所有者栅栏和 generation/端点。不代表持久注册表或完整生产运行时已通过。
- 本地无效结果描述符明确报告未派发；无效接收描述符仍保持未知。

## [1.0.0-alpha.19] —— 2026-09-05

### 新增

- 新增公开的 `NPS.Core.Alpha19` 策略入口，并可执行全部 47 条共享 NCP/NWP/NIP/NDP/NOP 加固向量，覆盖负向路径与 fixture 之外的边界分支。

### 变更

- 将 Node L2 扩展到 v0.7/38 cases，纳入当前 AaaS L2-01..L2-07；L2-01..05
  为强制项，L2-06/07 记 `na` 时必须携带有理由的 message。
- 将 Node L2 manifest 对齐 v0.6 与全部 31 个 case；validation 现会强制校验
  summary 完整性、整族适用性以及 single-/multi-Anchor HA 互斥约束。
- 注册 alpha.19 新增的协议错误码；现有 NativeAOT smoke 在候选代码上保持通过。

## [1.0.0-alpha.18] —— 2026-08-15

### 新增

- 新增官方有状态 LLM context DTO、进程内 context store 与 Action Server coordinator，覆盖 owner 隔离、CAS reservation、生命周期 action、真异步执行、取消和 19 个共享一致性向量。
- 新增 generated frame codec 的 NativeAOT publish-and-run smoke host，覆盖 nullable `UInt64` 往返。

### 变更

- 与其余五个 SDK 家族统一 unary request correlation、LLM usage 记账、严格有状态请求校验与任务所有权。

## [1.0.0-alpha.17] —— 2026-08-02

### 变更

- Core、NWP、NIP 与 NDP 的内置帧注册路径改用源生成 JSON 和 MessagePack metadata。
- Tier-3 BinaryVector metadata 改用有界 MessagePack/JSON 值树 writer，不再动态序列化
  `object`。
- 新增 `AddNcpHandshake()` 与 NativeAOT 安全的 `AddNwp()` registry 扩展。
- 实现共享的 NCP 0.11、NWP 0.20、NIP 0.13、NDP 0.12 与 NOP 0.9
  可移植 Profile，并执行语言无关一致性 fixture。

### 修复

- Tier-2 现在会保留 `JsonElement` 内容，不再将其序列化为空 map。
- Ivy NativeAOT 发布不再产生 NPS 自有的动态 JSON 或 MessagePack resolver 诊断；
  运行时生成的注册重载继续作为显式兼容 fallback 保留。
- SQLite storage 显式固定到 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`，并删除
  `GHSA-2m69-gcr7-jv3q` suppression。

## [1.0.0-alpha.16] —— 2026-07-23

### 变更

- 将 alpha.15 的 .NET 包族重新签发为 alpha.16；alpha.15 package coordinates
  已发布不可变，因此用 alpha.16 保持套件跨仓库统一版本。

## [1.0.0-alpha.15] —— 2026-06-28

### 变更

- 套件级 alpha.15 同步：对齐包元数据、当前 README / 版本 banner、分发源树以及 release-prep 说明到 NPS-Dev。
- 承载源事实树中的 NCP Tier-3 BinaryVector、入站 NWP Bridge server 加固、NIP canonical trust/revoke，以及 NDP discovery canonical-form 对齐。

## [1.0.0-alpha.14] —— 2026-06-26

### 新增

- `NPS.NWP.Bridge` 现在内置 HTTP/HTTPS、gRPC JSON unary、MCP JSON-RPC、A2A JSON-RPC 出站 dispatcher。
- 新增 `BridgeNode`、`BridgeDispatcherRegistry`、`IBridgeDispatcher`、`BridgeNodeMiddleware`、`AddBridgeNode`、`UseBridgeNode`，同时支持宿主无关调用与 ASP.NET 托管。
- 新增 `BridgeEndpointValidator`，默认启用 private/loopback SSRF 防护，并提供 URI-aware `allowed_prefixes` 校验。
- 新增 `HealthProbeRenderer`，支持 transport-neutral daemon health/readiness 输出。
- 新增 `TrustFrameValidator`，提供开源 pinned-grantor TrustFrame 基础验证。
- `NpsFrameCodec` 新增 `CreateDefault()`、`Create(...)` 与实例 `Peek(...)`。
- 新增 `NcpServerOptions`，支持 pre-preamble authenticated stream hook、handshake read timeout 与 Hello payload size 上限。
- `NipIdentVerifier` 新增 live revocation hook：`NipRevocationCheck`、`NipVerifierOptions.RevocationStore`，并让 OCSP transport failure 默认 fail-closed。
- `INipCaStore` 新增 `ListAsync()`，并提供 package 内置 `InMemoryNipCaStore`，用于测试、demo 与本地 dev stack。
- 新增 `NipCaClient` 类型化远程 CA client，覆盖 discovery、CRL、Ed25519 注册/续期/吊销/验证与 RFC-0002 X.509 注册。
- 新增 `NwpNativeNodeServer`，让 Memory / Action Node 可以通过 `NcpSession` 或任意 NCP stream 服务 native-mode NWP traffic。
- 新增 `LabAcacia.NPS.Conformance`，提供 Node L1/L2 case catalog、run manifest 与 CI 自认证 validation helper。

### 变更

- 公开包项目现在产出启用 SourceLink 的 `.snupkg` 符号包。
- NuGet 发布 workflow 现在打包并校验完整公开 .NET 包族，包含 `LabAcacia.NPS.Conformance`。
- `ActionNodeMiddleware` 与 `ComplexNodeMiddleware` 避免 nullable `CapsFrame.AnchorRef` 赋值。
- NIP CA CRL 响应现在包含 `issued_at` 与 CA detached signature；CA discovery 的 OCSP URL 指向已映射的 verify endpoint。
- SDK test gate 的测试/构建输出已清理为无 warning。

### 文档

- 新增 Bridge target schema 文档，并扩展 .NET quickstart：Core codec、DI、TrustFrame 验证、observability、storage、native NWP serving、Bridge、ingress、remote CA 与 conformance packages。

## [1.0.0-alpha.2] —— 2026-04-19

### Changed

- `NPS.Core` README 显式列出全套 NCP 帧（AnchorFrame / DiffFrame / StreamFrame / CapsFrame / HelloFrame / ErrorFrame）。
- `NPS.NWP` README 列出四种节点类型（Memory / Action / Complex / Gateway）。
- 状态：495 测试全绿，包含新增的 wire-size 基准、Gateway Node 中间件、A2A Bridge。

### 涵盖模块

- NPS.Core / NPS.NWP / NPS.NIP / NPS.NDP / NPS.NOP

---

## [1.0.0-alpha.1] —— 2026-04-10

作为 NPS 套件 `v1.0.0-alpha.1` 的一部分首次公开 alpha。

[1.0.0-alpha.2]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.1
