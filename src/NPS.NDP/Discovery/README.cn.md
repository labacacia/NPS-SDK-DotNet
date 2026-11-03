[English](README.md) | 中文

# 显式启用的结果发行节点发现

`NdpResultDiscovery` 是 .NET 的易失参考组件，须显式选择。它不替代现有内存注册表、
`NdpRegistryProfile` 或 registry daemon，不声明 `nps.production_runtime`，不修改 wire
字段或默认能力声明。[决定与边界](../../../../../docs/adr/2026-11-03-ndp-volatile-result-routing.md)。

宿主须从可信的当前 NIP/所有权策略中独立解析 `NdpRouteAuthority`，授权发行者公钥、
集群、正数 epoch、anchor 角色、精确 HTTPS 端点（含服务路径）、SHA-256 证书指纹、
策略修订和失效时间。策略不可由公告字段、调用者布尔值或模型输出生成；不可用即拒绝。

`Admit(ReadOnlySpan<byte>)` 限制 JSON 为 64 KiB、深度 16，拒绝重复字段，仅验证可移植
canonical Ed25519 签名。最多保留 256 个发行者的序号/摘要及集群 epoch 栅栏；不淘汰
过期栅栏。满容量拒绝新发行者。TTL 为 0–900 秒，0 表示下线；同一发行者不能在当前
注册表实例中切换集群。

签名 timestamp+TTL、接收时刻+TTL、当前身份失效时间及接收时的单调时钟共同限制租约。
未签名 last_seen 只能缩短，draining 在同一签名序号下不能被 healthy 清除。重复公告
不能续租；更改记录需要更高的签名序号。最高所有者失效、下线、排空、失去授权或缺少
能力提示时，不回退到旧所有者。记录过的同 epoch 冲突须以更高的授权 epoch 恢复，
即使一方已过期；这个局部规则比只检查存活冲突更保守。较低所有权 epoch 也使用已有
`GraphSeqRollback` 错误码。

`Resolve(cluster, requiredCapabilities)` 返回不可变的本地选择，或抛出带已有 NDP
错误码的 `NdpDiscoveryException`。Capabilities 只是粗粒度提示，不授予功能修订或
资源访问权。每次使用前调用 `ValidateCurrent(selection, requiredCapabilities)`，
重新检查当前策略、新鲜度、最高所有者和精确准入/实例绑定。结果与其原始选择一同保存。
端点、NID、epoch、序号、证书、公钥或策略变化均使旧绑定失效。按新选择创建新的
`ResultHttpClient` 并实际设置 TLS 指纹；客户端每次操作都检查经认证的 NWM 及所需
功能修订，接收端仍检查当前 NIP 和资源策略。

这不是跨网络原子租约。校验后端点仍可能变化，接收端准入及未知结果处理不可省略。
旧结果不能静默重绑到接替节点。报告 StateUnavailable、陈旧路由或结果 NOT-FOUND，
不得假定存在复制状态、自动新建结果或重放修改。未知操作保留原 UUID/查询/TTL，交由
所有者显式恢复。注册表冷启动产生新 generation，不恢复遗忘的历史栅栏，须再次独立
配置当前所有权策略。不声明持久恢复、联邦、状态迁移、原生传输或其他 SDK 对等支持。

[可执行静态/双节点拓扑](../../../../../docs/samples/ndp-result-routing.cn.md)。
