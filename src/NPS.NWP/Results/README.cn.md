[English](README.md) | 中文

# 进程内结果资源

`ResultResourceStore` 实现不可变快照、有界读取、精确 COUNT/Int64 SUM 和所有者释放，
对应[结果资源 revision 1](../../../../../spec/profiles/data-results.cn.md)。
`ResultActionProvider` 复用现有 HTTP Action Node 的 `/invoke`。
`ResultHttpClient` 提供有界 HTTPS 客户端。D20-02 的实验性参考 AC 为 COMPLETE，D20-05 示例集成为 IN_PROGRESS；
这些 API 不会自动公告或证明远端支持。

## 宿主负责的边界

宿主负责一致快照、身份认证、NIP 能力和节点/动作范围、实时行列/资源授权、规范请求摘要
及模型出站策略。`X-NWP-Agent`、结果引用和 NIP TrustFrame 都不是资源权限。
原生会话仍不支持。默认 direct HTTP 模式拒绝身份投影；显式 `projectedHttp: true`
可接入 NIP revision-1 的 [HTTP 绑定](../../NPS.NIP/Invocation/README.cn.md)，要求认证连接、
完整请求验证和持久重放。已测的接收端一跳不代表上游网关、原生或六 SDK 全部合格。

创建存储必须提供实时资源策略。创建 provider 必须提供有限来源白名单和
`ResultActionVerifier`。每个 `ResultActionSource` 的 `Validate` 必须是纯验证，
在访问来源前拒绝不支持的 query/filter/fields/order；`Capture` 必须在同一一致快照中
应用查询和实时行策略，以稳定记录 ID 消除排序歧义，返回来源版本、SHA-256 schema 引用
和有限记录。存储复制记录，来源的 JSON document 随后可释放。

验证器返回 `ResultActionAdmission`：已验证的 `ResultPrincipal`、由服务端按
NWP §9.7/NIP §7.8 计算的逻辑请求摘要、服务端确定的 `ToModel`。
`ActionContext.HttpRequestBody` 保留原始字段是否出现；不要用已插入默认值的
ActionFrame 重序列化代替原始请求验证。验证器必须检查持有证明、`nwp:action` 和确切
节点/动作范围，入场和执行前分别调用。存储在每次操作和重放时重新检查来源、授权、列
及出站策略，元数据和回执同样受保护。无效身份返回 null；验证器或策略不可用时关闭访问。

身份验证后，入场调用 `ValidateResourceAccess`，在分发 handler 前检查实时来源/接收者/
列/出站权限、已有创建键冲突、已满容量、快照/schema/游标绑定、到期/释放，以及单行
页面大小。它不捕获来源、不释放结果、不预占容量、不推进游标，也不消耗 NIP 重放记录。
执行时仍复检权限，并在发布快照时原子检查实际行数/字节预算；并发变化仍可能导致执行
失败。页面探测只在服务内物化最多一行已选列，不向调用方返回受保护内容。
未知动作在 feature 入场后返回 profile 的有界 422；其他 Action Node provider 默认仍为
原有 404，除非显式选择覆盖。

投影请求还须在 NIP `Authorize` 回调内调用 `ValidateResourceAccess`：在 claims 已验证、
操作纯验证通过之后，持久重放入场之前。只传宿主确定的主体/租户/策略版本/出站决定和
已验证摘要。仅在 `VerifyAsync` 之后检查，无法保证策略拒绝不消耗投影。接入方法见
[NIP 模块说明](../../NPS.NIP/Invocation/README.cn.md)。
[不变量执行记录](../../../../../docs/benchmarks/result-invariant-observation-2026-11-02.cn.md)
列出全部 39 项冻结语义到 HTTP 夹具的映射及身份验收边界。
[资格ADR](../../../../../docs/adr/2026-11-02-reference-result-binding-qualification.md)
区分实际投影HTTP参考与未合格的direct/native路径。原生动作先调用provider授权；
结果provider在原生handler/验证器/捕获前返回feature不可用，不产生原生offer。

适配器默认关闭。`qualifiedHttpRouteEnabled` 必须每次确认本地启用及整条路由的全部后端
保证。通过 `ConfigureNode` 和既有 `AddActionNode`/`UseActionNode` 注册动作，不产生
NWM feature offer。真正远端启用前须在 D20-03 验证真实身份、认证路由、客户端入场和
全部后端。当前 HTTP 测试使用明确标注的合成身份/摘要夹具，只证明中间件与存储连接。

## 操作与支持子集

请求使用 `application/nwp-frame`。direct 模式只有一个精确的
`X-NPS-Required-Features: nps.data_results=1`；显式 projected 模式要求
`nps.data_results=1,nps.invocation_context=1` 和单一 NIP 投影头。
成功返回 `application/nwp-capsule` 的 CapsFrame 和精确选择回显；失败返回
`application/nwp-error+json` 的 ErrorFrame，不回显成功选择。
格式错误、重复、遗漏、不支持或已撤回的 feature 在捕获来源前被拒绝。
请求最多 65536 字节；重复解码键、未知 profile 参数都拒绝。

| 动作 | 参数和行为 |
| --- | --- |
| `result.create` | `source_id`，来源纯验证支持的 `filter/fields/order`，可选 `ttl_seconds`；ActionFrame 必须带 UUID-v4 `idempotency_key`。只返回 descriptor。 |
| `result.read` | `result_ref/snapshot_id/schema_ref`，必填 `limit`（1..1000），可选 `fields/cursor`。返回 descriptor、有界记录和游标。 |
| `result.aggregate` | 三项绑定与 `aggregate.operations`、可选 `group_by`。必须有 `{func:"COUNT",alias:"count"}`，可选一个 `{func:"SUM",field:"amount_minor",alias:"sum"}`。最多四个分组列，不能名为 count/sum。其他函数、别名、COUNT-field、having 均拒绝。 |
| `result.release` | `result_ref`。所有者幂等释放，删除记录载荷。 |

JSON 调用示例见[英文版](README.md#operations-and-exact-supported-subset)，同样复用现有
ActionFrame/CapsFrame，没有新帧或端点。

## 生命周期与恢复

可用 `ResultResourcePolicy(issuerNid)` 安装服务端拥有的 `ResultSourceAuthority`，再将
`policy.Authorize` 传给存储。来源、委托和模型出站默认拒绝。宿主必须使来源权限反映
当前所有者 NIP/来源策略，在撤销或策略改变时更新/禁用；该类不自动订阅 CA。

经所有者认证查找后，`store.GetOwnerGrantScope(owner, resultRef)` 返回不可变的进程内输入，
供 `policy.IssueGrant` 使用。宿主明确指定已验证接收者、read/aggregate 操作、收窄的列、
不晚于原结果的有效期及可选模型出站许可。这不是远程动作；接收者不能获取所有者 scope、
递归授权、释放所有者结果或访问同来源的其他结果。grant ID 只用于管理，不是凭证。
`RevokeGrant` 检查所有者/租户；来源或委托权限撤回也会阻断已有接收者访问。
行策略版本改变后必须明确重新捕获，不会偷偷重写快照或复活旧授权。

策略状态有独立的有界额度：128 个来源权限、4096 条授权、16 MiB 序列化逻辑元数据
（另计入固定开销）。不驱逐活授权，过期/撤销后释放计费。没有新增持久/复制承诺。
见[接收者策略 ADR](../../../../../docs/adr/2026-11-02-result-recipient-policy.md)。

默认 TTL 300 秒，最多 900 秒，墙钟回退不延长剩余寿命。每个所有者最多 16 个活结果；
每结果最多 100000 行/32 MiB；记录、元数据和重放状态合计逻辑预算 128 MiB。
元数据/释放墓碑另有 4096 条上限。配置只能降低这些限额，并发发布原子检查容量，
不会驱逐承诺期内的活结果。读取/聚合结果在 capsule 包装和压缩前最多 64 KiB。
单行过大、不支持的值或聚合溢出会明确失败。

捕获复用列名验证集合，使用每次捕获自己拥有的 writer 缓冲复制 UTF-8，在行间及最终
数组框架写完后检查字节数，最终仍保留精确、独立的字节数组。这样避免在共享序列化
缓冲池中暂存整份数据。逻辑留存字节限制不等于进程 RSS：宿主来源捕获、临时复制、
TLS/密码学和运行时内存仍须独立预算与测量。

读取在保留的 UTF-8 数组上逐行前进，每页/聚合不再为整个结果构建 DOM。页面独立序列化
所选值，聚合仅为有限的不同分组保留一次标量键。存储仍是不可变载荷副本，不代表实时流
或 delta 能力。

provider 自行管理重放，绕过通用 ActionNode 的 24 小时缓存，以免缓存绕过结果 TTL、
权限或租户。进程 TTL 内，同创建键和摘要返回仍有权限访问的 descriptor；改变摘要冲突。
释放后仅保留有界重放元数据直到过期。来源或授权撤销会阻断后续访问，但不能收回已交付数据。

过期、释放、重启、无法证明同一快照的接替节点都返回不可用。重新查询明确创建新快照。
schema/快照/游标错配要求显式 rebase，不能偷偷切回实时数据。发布后取消可能留下结果，
应以原创建键在 TTL 内恢复，不能声称未分配。未实现外部业务写入、持久恢复、派生结果
操作或实时订阅。审计只记录有限的已验证主体、动作、结果和哈希标识；禁止记录完整行、
请求、提示词、凭据或签名材料。可选 `ResultOperationAudit` 回调仅交付已验证租户/主体、
动作、结果分类和引用哈希，由宿主配置接收器。审计失败关闭访问，但可能发生在分配之后，
不能据此声称已经回滚。

## 公开 HTTP 客户端与显式能力发现

使用 `ResultHttpClient(baseUri, expectedIssuerNid, signer?, configureTransport?)`，
固定 HTTPS 节点路径和签发节点。transport 回调用于配置 TLS/mTLS 信任和凭据；
客户端随后强制禁用重定向和 cookie，并拥有、释放自己的连接。
提供 signer 才选择投影模式；它收到实际帧、固定目标和完整功能需求，必须基于独立验证
的上游身份签名。direct 模式也需配置其认证连接，不会降级到匿名或其他端点。

每次操作先获取同一路由的 `/.nwm`，验证节点、版本响应头、认证与动作能力、固定调用 URL
及明确的 revision-1 feature offer。发现失败时不签名、不 POST；已观察版本回退或同版本
内容替换也拒绝。成功必须具有精确 selected-feature 回显和有界、类型正确的载荷。
规范错误转换为 `ResultHttpException`，只暴露 `HttpStatus`、`NpsStatus`、`ErrorCode`、
`OutcomeUnknown`，不泄露原始响应。没有自动重试或自动翻页。派发后取消不代表回滚。

```csharp
// 地址、身份和凭据来自可信宿主配置，不接受模型生成的调用地址。
using var client = new ResultHttpClient(resultNodeBaseUri, resultNodeNid,
    signAdmittedCallerRequest, configureMutualTls);
var logicalCreateKey = Guid.NewGuid(); // 派发前保存，供结果不确定时恢复。
var result = await client.CreateAsync("orders", logicalCreateKey, ct: ct);
var summary = await client.AggregateAsync(result, ["reason"], "amount_minor", ct);
var page = await client.ReadAsync(result, 20, ["id", "reason", "amount_minor"], ct: ct);
await client.ReleaseAsync(result, ct);
```

创建结果不确定时，在保留期内以原逻辑键、相同参数和新身份凭证显式恢复；不要自动换键。
`OutcomeUnknown=false` 仅描述本次尝试，不能否定之前已发生的执行。

取消以 `OperationCanceledException` 传播；POST 已可能发出后，即使客户端未收到响应，
创建也可能已经分配结果。此时按本次结果不确定处理，在留存期内用原键/参数、新凭证和
当前权限显式恢复。取消不证明回滚，客户端不会自动重试。真实双跳 mTLS 用例在发布结果
后阻塞回复，取消公开调用方，再验证仅一次捕获即可原键恢复。

宿主可以显式配置 `ActionNodeOptions.ManifestSource`，通过 `ActionNodeManifestSource`
提供不可变完整 NWM。宿主负责跨重启持久化递增 `ManifestVersion`，并协调 offer、路由及
所有后端的原子更新。`/.nwm` 返回 `X-NWM-Version`，支持整数 `If-None-Match`/304；
动作/schema 发现使用相同来源。元数据验证不等于后端合格证明，注册结果动作不会生成 offer。
参见[发现与客户端 ADR](../../../../../docs/adr/2026-11-02-result-http-discovery.md)。

## 验证与缺口

运行 NPS.Tests 的 `ResultResourceStoreTests`、`ResultActionHttpTests` 和
`ActionNodeMiddlewareTests`。HTTP 测试检查真实 capsule/error、快照分页、COUNT/SUM、
释放、复制引用、撤销、过期、模型出站、入场拒绝和来源调用计数；原有动作测试保护默认
缓存行为。完整上游 NIP/委托/桥、原生入场及六 SDK 一致性仍待验证。
另有 `ResultInvocationTlsTests` 通过真实 mTLS、Ed25519 和文件重放验证整数参数投影接收端，
并使用显式测试 offer 验证公开客户端、能力撤回、版本/重定向限制、畸形/超大响应、回显丢失
恢复和结构化错误。这不代表生产网关或整个协议套件合格。

双跳 reference 还通过 `NipResultHttpProjector`、实际公开 mTLS 主体、CA 签名证书和
SQLite CA 状态验证网关。每次访问都复查原主体及注册来源所有者的撤销，包括接收者既有
grant。该宿主集成模式不等于自动生产 PKI 或所有通用 bridge 迁移。
真实用例还拒绝接收者对未授予列的 SUM，允许已授予 COUNT，再在撤销后以零新增分发
拒绝。来源权限或所有者 CA 撤销后，原始及过滤来源结果的读取/聚合都拒绝。这不代表
新增派生结果 TTL 或实时结果流合格。
`SendFrameAsync` 保留确切转发 JSON，并执行相同发现/feature/错误边界；其 JSON 返回值
不执行 typed 便捷方法的 descriptor 一致性检查。参见[网关 ADR](../../../../../docs/adr/2026-11-02-result-gateway-projection.md)。

[冻结 oracle 观察](../../../../../docs/benchmarks/result-oracle-observation-2026-11-02.cn.md)
记录了经该公开路径执行的 100/1000/100000 行退款工作流。精确整数分组、有界证据记录、
溯源、快照冲突和释放均对照共享冻结答案验证；该正确性证据不替代 C/D 传输及模型实测。

参见[运行时缓存 ADR](../../../../../docs/adr/2026-11-02-result-action-replay.md)、
[D20-02](https://github.com/labacacia/NPS-Dev/issues/166) 和
[D20-03](https://github.com/labacacia/NPS-Dev/issues/167)。
