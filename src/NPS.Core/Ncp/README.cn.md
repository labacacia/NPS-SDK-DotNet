[English](README.md) | 中文

# NCP参考传输与schema批次接收器

NcpNativeClient/Server负责原生握手和编码策略，NcpSession向上层提供已认证传输，不产生
资源权限。新候选feature须两端显式启用，并在实际双向认证TLS会话精确选择。

实验[批次候选契约](../../../../../spec/profiles/schema-batches.cn.md)及
[CR-0017](../../../../../spec/cr/NPS-CR-0017-schema-batches.md)复用现有编码帧。
SchemaBatchReceiver是本地有界状态机，不是公告。调用方提供FeatureSelection
不证明准入；启用前宿主须验证实际协商、生产者/来源权限、分配上限、编码策略、串行
入口与消费者生命周期。实际codec测试证明对象JsonElement仍为含字段名的MessagePack map；
新接收器只接受显式有序数组，不修改旧序列化或自动转换对象，不推导token/供应商费用。

NcpNativeClientOptions提供宿主认证hook及本地实验开关，NcpHandshakeProfile控制服务端启用。
实际Hello/握手Caps在返回会话前验证CR-0012字节上限及精确选择；未知required拒绝，可选
未支持项省略，旧端漏回显不能满足要求。SelectedFeatures只属于当前连接；会话工厂施加
协商payload/编码上限，拥有并在关闭时释放一个有效接收器。宿主仍须实时来源授权、异常
断连/撤权时取消释放，见[绑定ADR](../../../../../docs/adr/2026-11-02-schema-batch-session-binding.md)。

## 处理与失败

每个已验证生产者/来源/订阅使用独立接收器。先收有序已验证Anchor，再收连续有界完整Stream。
暂存块不公开，终块发布版本0的不可变快照。Diff的EntityId/AnchorRef/BaseSeq必须匹配epoch/
schema/版本，只允许原子替换既有类型化单元格，成功后版本加一；已交付旧快照不变。
单次Caps无版本，不能建立差量基线。

Receive只接收一个完整编码帧，解码前检查长度，拒绝重复map成员、被归一化前的浮点token、
过深嵌套、binary/extension强转、FINAL不符及非法位置数据。TTL同时检查绝对和单调时间。
生产者不能扩大接收信用；消费者处理后调用GrantCredit，再沿实际反向绑定发送返回控制帧。
Diff同步处理，无更新队列；基线credit不等于通用订阅/传输调度器。

帧故障/取消终止并清空数据；Dispose不能撤回已交付不可变值。重连/失败后须重新授权接收器，
以新epoch完整基线恢复，不能猜遗漏差量。宿主须在权限撤销、断连和消费者终止时取消/释放。
硬限额及类型/patch子集见SB-01..08。留存字节包括外层数组标点，不等于RSS或总分配；解码、
暂存及原子发布可能共存，须测量真实成本。

持有的行从同一份计量后的紧凑字节解析。原始JSON回归先复现Clone保留12000个未计量空白
字节；改为持有紧凑表示后消除。这里不宣称任意数据的RFC 8785规范化。

## 已执行及剩余证据

SchemaBatchStreamPublisher/Reader在专用已选择会话串联这些边界：宿主回调在捕获及每次
读/写/交付前授权。生产者首批仅发一个数据帧，再等有界反向credit；消费者排空窗口再补充。
零credit暂停，epoch/水位错误、重放、非空或超额控制帧终止。无后台应用队列，credit/帧等候
有界，调用方取消继续有效；捕获是宿主持有的不可变数据。

PublishAsync只返回本地写计数，不证明对端提交；后续差量回执的BaselineTupleBytes仍指
基线。ReadNextAsync先返回完整不可变基线，再逐次返回确切基线的更新。I/O、超时、权限或
取消故障关闭专用会话、清空接收器；不能撤回已交付数据，也不自动重试。断流后须重新授权
完整基线。实际夹具关闭TLS会话恢复，首版参考不宣称已验证恢复会话的双向身份。

45项真实编码帧/表示测试通过：完整发布、旧版本不可变、epoch/schema/基线不符、缺号/重复
块或更新、原子patch拒绝、顺序/类型/null/不安全整数、两种时钟到期、信用/溢出/伪刷新、
取消、留存边界、重连oracle、超大/多余/重复/null wire及浮点强转。

18项真实原生会话用例通过：固定pin的mTLS及公共API、两种编码、实际反向credit/基线/差量、
开关/明文拒绝、重连、非法原始Hello、旧端/恶意选择及metadata/生命周期。MessagePack把
修订1.0归一化为1的准入缺口先失败复现，修复后在应用数据前拒绝。

共享语料新增106项固定JSON/MessagePack字节用例，由接收器直接执行。省略patch_format及
显式value:null暴露DTO解码缺口；profile保留原始字段存在性，缺少value仍拒绝。规范
MessagePack基线也先在旧PascalCase信封失败；Python/TypeScript源实现使用snake_case。
SchemaBatchFrameCodec及带feature的握手Caps对两种编码使用同一规范字段名。会话编码API
施加实际选择/编码/载荷/生命周期；旧codec保留。接收器先验证wire token再做有界JSON
投影/类型解码，测量须包含这些分配和CPU；runtime使用现有MessagePack库处理二进制。

新profile的schema ID使用SchemaBatchFrameCodec.ComputeAnchorId，复用既有有序Anchor
结构及调用绑定已经使用的严格JCS字符串逻辑。10项独立Unicode/HTML/控制字符/不归一化
用例链接到未修改的106项wire冻结；两种编码最初仅2/20通过，严格转义后20项通过。孤立
UTF-16代理项拒绝，未选择的旧AnchorIdComputer转义行为保留。实体化schema包含
nullable=false、省略null semantic；此API不是任意JSON的JCS实现。

候选注册采用[追加继任](../../../../../docs/adr/2026-11-02-additive-schema-batch-registration.md)，
保留原结果冻结证据。通用NIP/来源权限、来源规模/资源及同边界压缩/替代格式仍待验证，状态
IN_PROGRESS。上述测试不证明原生结果动作、NWP实时结果流、其他SDK或发布合格。

另有22项真实传输用例：两种编码的100/1000/100000行ID/总和oracle、绑定差量/旧状态不变、
零窗口暂停恢复、错误/伪造/重放credit、来源/消费者拒绝、捕获前及阻塞中取消、等credit超时、
超大行、部分EOF及新会话恢复。这是预置对端/来源策略，不代表通用NIP授权；本修订的CPU/
分配/峰值及对照效率仍待实测。
