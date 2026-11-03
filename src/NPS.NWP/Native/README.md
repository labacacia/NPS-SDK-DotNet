English | [中文](README.cn.md)

# Native NWP action admission

`NwpNativeNodeServer` dispatches decoded Query/Action frames over an established
stream. The host owns authenticated transport and any claimed NIP identity/session
binding. Stream authentication alone does not authorize a node, action or result.

For registered actions, the server builds one `ActionContext`, invokes
`IActionNodeProvider.AuthorizeAsync`, then passes that same context to `ExecuteAsync`.
Provider-controlled `ActionNodeException` becomes the existing ErrorFrame with its
bounded public status/code/message. Denial occurs before Execute entry. Providers
with the default no-op authorizer retain their existing behavior; protected providers
must implement current authority checks. An HTTP context is absent on this path.

The current `ResultActionProvider` qualifies only its separately tested HTTP reference
path. It declines a native call before its identity verifier, handler or source capture,
even when its HTTP route gate is true. This guard adds no native result identity,
feature negotiation, endpoint, replay store or feature offer. Candidate native result
contracts remain unqualified. See the
[binding qualification ADR](../../../../../docs/adr/2026-11-02-reference-result-binding-qualification.md).

`NwpNativeNodeServerTests` uses actual frame codecs and a scripted duplex stream:
authorization-before-execution, canonical controlled denials and JSON/MessagePack
result-route rejection. Those are framed dispatcher regressions, not native network,
mTLS/NIP/session or six-SDK qualification. The real HTTP result topology is documented
in [Results](../Results/README.md).
