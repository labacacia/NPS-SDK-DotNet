[English](README.md) | 中文

# 实验性有界结果任务执行

只有显式设置 `NopOrchestratorOptions.RuntimeBinding` 才选择本参考路径；默认不启用，也不宣称完整
`nps.production_runtime`。绑定为 null 时继续使用原编排实现。

宿主为每个经过当前认证的调用者建立绑定，`CallerNid` 不能来自任务、模型断言或转发头。工作者适配器必须独立核验
通道/NIP 身份，逐帧附真实 peer；仅比较 `SenderNid` 不够。结果 API 仍独立核验当前 NIP/资源策略；任务上下文、
空 delegated_scope 和结果定位符都不会赋权。

执行先冻结任务，再在原子 `(caller_nid,task_id)` 键下比较不可变摘要，摘要不是唯一键的第三部分。对象属性按
UTF-16 ordinal 排序并复用 JCS 字符串编码；数字只接受安全整数，不冒充通用 JCS。派发前持久保存随机 UUID-v4
子任务/操作标识。重复运行中的任务返回 Running 且不附终态 execution_outcome；终态重复返回首个已提交结果。
不可变输入冲突为 `NOP-REPLAY-CONFLICT`，包括已接纳任务的非法改动。预取消不能覆盖既有确定性。

串行 DAG 复用 DagValidator/NopInputMapper，只把已证实完成的上游输出交给下游。逐帧检查真实 peer、任务/子任务、
固定 stream UUID、从零连续序列及仅终态允许的确定性。缺失、矛盾、畸形或丢失终态均为不可重试的 indeterminate，
并停止下游；这不表示没有发生效果。只有明确 not_dispatched 加 retryable 错误才能在原操作标识、剩余重试预算及
原期限内重试。已知终态失败不会仅因 retryable 断言再次执行。父任务确定性与完成/失败/取消状态分开，不盲目补偿、
不承诺回滚。

任务和节点期限均不因重试延长。即使工作者忽略取消，等待也有限；迟到效果不能更改已提交终态。取消不在接纳锁内
执行任意回调，也不覆盖完成任务。宿主负责隔离/停止不合作的外部进程；编排器不能撤销效果或强行结束远端进程。

参考上限：64 节点，任务64KiB，每次尝试16帧/64KiB，累计节点输出256KiB，任务最长120秒，结果TTL 1–900秒。
本路径在派发前拒绝 callback、preflight、condition、K-of-N 和补偿动作。远端 NOP、原生绑定、完整生产运行时及
六 SDK 对等资格仍未验收。

SDK 账本接口隔离存储责任；已有 NopDag 宿主样例提供 SqliteNopRuntimeJournal：显式首次初始化、单主机独占、
FULL 同步事务、永久去重墓碑、终态 CAS、256 任务容量且不驱逐，重开时缺文件/表即失败。样例固定 SQLitePCLRaw
bundle3.0.5，避免新增受影响的2.1.11本地库，参见[维护者包](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/)
和[安全公告](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)。不承诺共享副本、网络文件系统、防回滚备份恢复、
结果迁移或外部效果恰好一次。宿主须保护目录/备份，保留数据库与 `.owner` 标记；重启不悄悄新建历史，结果清理
不删除操作账本。

仅首次、全新专用绝对路径使用 `new SqliteNopRuntimeJournal(path,initializeNewFile:true)`；随后通过
`new SqliteNopRuntimeJournal(path)` 重开。已有历史不允许初始化，另一个存活宿主不能接管。确认旧宿主独占已释放
后，未决任务记为 indeterminate，不重新派发。已知终态不可改；TTL 隐藏输出但保留墓碑。跨宿主时钟回退和备份恢复
没有获得生产级保留保证。

在主开发仓库执行真实参考检查：

```bash
dotnet test impl/dotnet/tests/NPS.Tests/NPS.Tests.csproj -c Release --filter 'FullyQualifiedName~NopRuntimeExecutorTests|FullyQualifiedName~NopWorkersExchange|FullyQualifiedName~NopHandoffRejects'
```

四节点流程为 query-orders/query-refunds -> analysis -> report。Alice/Bob 两个宿主绑定的工作者角色使用不同真实
mTLS 证书及当前 NIP 身份完成每个数据网关/接收端请求；角色之间为本地派发，不宣称远端 AlignStream 认证已验收。
Alice 拥有快照，只向 Bob 授予精确快照、字段及 aggregate 权限；Bob 不得读行/释放。报告读取五行证据，最后由
Alice 撤销授权并释放已知快照。过期返回冻结的统一 NOT-FOUND；查询终态丢失则阻断下游，保留明确的未决结果。
确定性编排不调用模型。

[实现与剩余验收](../../../../../docs/plans/2026-11-03-nop-result-handoff-implementation.md)。
