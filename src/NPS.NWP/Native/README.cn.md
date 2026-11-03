[English](README.md) | 中文

# 原生NWP动作入场

`NwpNativeNodeServer` 在已有连接上分发解码后的Query/Action帧。宿主负责认证传输及
其声明支持的NIP身份/会话绑定，连接认证本身不等于节点、动作或结果权限。

已注册动作先构建一个`ActionContext`，调用`IActionNodeProvider.AuthorizeAsync`，再把
同一个context传给`ExecuteAsync`。provider主动抛出的`ActionNodeException`映射为
既有ErrorFrame的公开有界status/code/message；拒绝发生在Execute入口前。
默认无操作authorizer的provider保持原行为，受保护provider必须实现实时权限检查。
此路径没有HTTP context。

当前`ResultActionProvider`只资格验证了另行测试的HTTP参考路径；即使HTTP路由开关为真，
原生调用仍在验证器、handler和来源捕获前被拒绝。此防护不增加原生结果身份、feature
协商、端点、重放存储或offer，候选原生结果契约仍未合格。见
[绑定验收ADR](../../../../../docs/adr/2026-11-02-reference-result-binding-qualification.md)。

`NwpNativeNodeServerTests`使用真实帧codec和脚本双向stream，验证先授权后执行、
受控错误映射及JSON/MessagePack结果路由拒绝。这些是帧分发回归，不代表原生网络、
mTLS/NIP/会话或六SDK验收。真实HTTP结果拓扑见[Results](../Results/README.cn.md)。
