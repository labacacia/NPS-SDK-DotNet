[English](README.md) | 中文

# 实验性结果调用身份绑定

本模块实现 NIP §7.8 / NWP §9.7 的整数参数参考子集，不注册服务、不认证传输对端，
也不自动公告支持。D20-03实验投影HTTP参考AC已按
[绑定资格记录](../../../../../docs/adr/2026-11-02-reference-result-binding-qualification.md)完成；
其他路由/集成仍须独立证据，安装包不提供这些证明。

既有严格JCS字符串实现现与可选schema批次profile共享于NPS.Core.Serialization.JcsString。
调用规范字节、整数子集、域分离、权限及重放顺序不变；共享字符串逻辑不扩大调用schema，
也不增加签名方案。

## 宿主接入

1. 从独立认证的传输连接取得对端身份，例如真实 mTLS 客户端证书；按当前服务策略映射
   可用网关 NID。不能取自 X-NWP-Agent、转发证书头、模型或投影自称的 issuer。
2. 提供 `NipInvocationVerificationOptions`：本地开关、确切 audience/路由 target、
   已入场 feature 集、当前配置 Ed25519 公钥、权限上限、纯操作验证和实时主体/动作策略。
   Authorize 必须检查 `nwp:action` 及适用的节点/动作范围。
   在此回调中按已验证主体和宿主策略推导本地主体/租户/版本/出站决定，以精确验证后的
   动作/params/key 和已验证摘要调用结果 provider 的 `ValidateResourceAccess`。
   该检查不消耗资源，在重放入场前执行；仍保留 provider 后续入场/执行复检，不把预检当预占。
3. 提供合格的 `INipProjectionReplayStore`。SQLite 适配器没有自动注册或内存回退；
   同一 audience 在单机的所有副本必须共享同一专用文件，不支持多机或网络文件系统。
4. 用受信任宿主工厂创建 `NipResultHttpBinding`，将 HttpContext 和原始
   `ActionContext.HttpRequestBody` 传给验证器。主体取自已验证 claims；租户、策略版本和
   模型出站决定仍取自本地策略。通过 `Alpha20ErrorStatus.Mappings` 映射异常分类，
   不将原始错误细节、投影或请求放进 ErrorFrame。
5. 显式使用 `ResultActionProvider(projectedHttp: true, ...)`、当前全部后端路由保证和
   最小化审计回调。注册不产生 feature offer；真正远端公告前要验证客户端发现与所有后端。

请求仍是普通 ActionFrame，仅使用单一规范头：

```text
Content-Type: application/nwp-frame
X-NPS-Required-Features: nps.data_results=1,nps.invocation_context=1
X-NPS-Invocation-Context: <规范无填充 base64url NIP 信封>
```

HTTP body 不可混入原生 `invocation_context/required_features`。成功仍返回 capsule 和
精确选择回显；错误不回显成功。direct 模式是另一条绑定，继续拒绝投影请求。

宿主若保留规范结果拒绝码，只能在上述已验证策略回调中把本地预检错误放入私有、当前
请求状态；且仅当验证器报告策略拒绝 `NIP-CONTEXT-BINDING-MISMATCH` 时映射该错误，
并写最小化资源拒绝审计。不能覆盖签名/连接错误、身份或资源审计不可用、密钥/重放存储
故障或其他失败。不能用请求头/body 填充该状态，也不能跨请求保留。真实回环集成展示
这种映射；绑定库不会自动安装它。

## 摘要、验签与重放

`ResultActionRequest` 读取原始 JSON，保留字段是否出现；只移除规范规定的关联/载体/
外层已绑定字段。存在的 params、priority、async、callback、timeout 都参与摘要。
target 来自认证路由，不能用已插入默认值的 DTO 重序列化代替原请求。丢失响应后，
逻辑动作/创建键不变，但必须换新投影。

`AdmitAsync` 在原子重放入场前检查大小、schema、认证连接 issuer、配置密钥/签名、
接收端权限上限、时间、请求摘要和实时策略。`RevalidateAsync` 只继续同一宿主请求对象、
同一载体和摘要，重新检查当前权限/时间，不重复消耗 projection ID。
绑定把不可伪造的进程内入场对象放在私有 HttpContext.Items 中，禁止跨请求共享或序列化。

当前 schema 接受 ±9007199254740991 范围内的十进制整数字面量、字符串、布尔、null
及有界对象/数组；小数、指数、不安全整数、非法 Unicode、重复解码键都拒绝。
支持值的 JCS 排序/转义严格匹配，不宣称通用浮点 JCS、普通 query 或 byte-body 支持。
scope/capability 数组按无符号 UTF-8 排序，JCS 对象键另按 UTF-16 排序，不做隐式归一化。

## 存储、审计和验收边界

从 NPS.NIP.Storage.Sqlite 调用 `SqliteNipProjectionReplayStore.OpenAsync`，指定绝对、
应用拥有的文件路径及 audience/capacity。仅首次安装可传 `initializeNewFile: true`，
且文件必须不存在。通常重开要求已有合法状态；丢失、损坏、audience/capacity 错配全部
关闭访问。不能丢库后自动初始化、每副本另建文件或用新库跳过旧重放记录。

默认最多 65536 条，硬上限 1000000。立即写事务在锁内读时间，原子插入 issuer/id；
至少保留到 expires_at+5，不驱逐活记录，持久时钟栅栏拒绝回退。锁、容量、存储失败均为
验证器不可用。投影重放保护不等于结果 TTL/幂等，更不证明业务执行。

配置 `NipInvocationAudit` / `ResultOperationAudit` 的宿主审计接收器，只记录已验证主体、
动作、结果分类和结果引用哈希。未验证断言不提供审计主体；禁止完整 claims、行、计数、
列清单、原始引用、信封/签名、提示词或密钥。配置的审计失败关闭访问，但已经分配的
结果仍可能保留，不能据错误响应声称未执行。

运行 `NipInvocationJsonTests`、`NipInvocationVerifierTests`、`NipProjectionReplayStoreTests`
及 `ResultInvocationTlsTests`。后者使用真实回环 mTLS、签名投影、文件重放、公开客户端
及双跳网关操作。预配置临时证书 pin 和 SQLite CA 检查仅验证该明确拓扑，不是通用生产
PKI。公开 PKI、通用外部协议网关迁移、原生绑定、多机故障和六 SDK 一致性仍须单独完成。
projector 测试还验证精确授权上限、身份/密钥撤回、参数边界和取消。

## 公共网关生成新投影与实时撤销

使用 `NipResultHttpProjector` 时，宿主先对公开一跳执行精确 feature 入场和有界请求验证。
提供原始公开 HttpContext、确切下游帧、固定接收节点和下游功能对；projector 移除公开
投影头，在实时密钥查询前后执行强制认证，限制精确能力/scope 上限和证书剩余寿命，
再签发新的随机投影 ID。宿主拥有密钥生命周期，构造器不注册端点或 feature offer。

Authenticate 回调必须验证实际 TLS/mTLS/会话主体、NIP 证书、当前 CA 撤销、能力以及
确切节点/动作范围。`X-NWP-Agent`、复制的公开 IdentFrame 或投影本身均不足以认证。
返回的 `NipGatewayAuthority` 是服务端状态，不能从请求反序列化。接收端也必须检查
原始主体及源所有者的当前 CA/资源权限；未过期的网关签名不能绕过后续撤销。

转发使用 `ResultHttpClient.SendFrameAsync` 保留原 JSON 字段出现情况及语义；每一跳分别
发现、验证自己的精确 feature 集合，公开响应仅回显公开一跳选择。禁止转发来路投影、
调用者地址/密钥或下游原始响应头。回复丢失仍是不确定执行，只能显式以原逻辑键和新投影恢复。

真实双跳 fixture 使用独立预配置的公开/接收端 mTLS pin、CA 签名 IdentFrame 和 SQLite
CA 状态。对其唯一注册来源，每次操作都复查所有者 CA 权限，包括接收者的既有 grant；
CA 不可用则关闭访问。有效所有者不能自动恢复已单独撤回的来源。库没有新增全局 CA
订阅服务。参见[网关 ADR](../../../../../docs/adr/2026-11-02-result-gateway-projection.md)。

参见[ADR](../../../../../docs/adr/2026-11-02-result-invocation-http-binding.md)、
[结果 profile](../../../../../spec/profiles/data-results.cn.md)、
[D20-03](https://github.com/labacacia/NPS-Dev/issues/167)。
