English | [中文](README.cn.md)

# Experimental result invocation binding

These components implement the integer-only reference slice of NIP §7.8 / NWP §9.7.
They do not register a server, authenticate a transport peer or advertise support.
D20-03 experimental projected-HTTP reference AC are complete under the
[binding qualification record](../../../../../docs/adr/2026-11-02-reference-result-binding-qualification.md).
Other routes/integrations need their own evidence; package installation supplies none.

The existing strict JCS string primitive is now shared with the optional schema-batch
profile in NPS.Core.Serialization.JcsString. Invocation canonical bytes, integer-only
limits, domain separators, authorization and replay ordering are unchanged. This shared
string primitive does not expand the accepted invocation schema or add a signing scheme.

## Integrate in the serving host

1. Obtain independently authenticated channel identity. Resolve allowed gateway NIDs
   from current host policy (for example, actual mTLS peer certificates), never from
   X-NWP-Agent, forwarding headers, the model or the signed projection's own issuer.
2. Supply `NipInvocationVerificationOptions`: local enablement, exact audience/route
   target, admitted feature set, configured live Ed25519 key lookup, delegation ceilings,
   pure registered operation validation and current subject/action authorization.
   Authorize must require `nwp:action` and the exact applicable node/action scope.
   In that callback, derive the local principal/tenant/revision/egress from trusted
   policy and the verified claim subject, then call the result provider's
   `ValidateResourceAccess` with the exact validated operation/params/key and verified
   request digest. This non-consuming check runs before replay claim. Keep the
   provider's later preflight and execution checks; a preflight is not a reservation.
3. Supply a qualified `INipProjectionReplayStore`. The existing SQLite adapter has no
   automatic registration or fallback. All replicas for this audience on one host must
   share the same dedicated file; it is not a multi-host/network-filesystem adapter.
4. Create `NipResultHttpBinding` with the trusted host options factory. In the result
   provider's verifier, pass HttpContext and `ActionContext.HttpRequestBody` directly.
   Derive tenant, policy revision and model egress from local policy; derive subject
   from the admitted claims. Map `NipInvocationException.Code` through the existing
   `Alpha20ErrorStatus.Mappings` into the standard ErrorFrame without raw error detail.
5. Use `ResultActionProvider(projectedHttp: true, ...)`, with the current all-backend
   route gate and optional minimized audit callbacks. Registration installs no offer.
   Test discovery/admission and every claimed backend before any remote advertisement.

HTTP requests require exactly one header of each kind:

```text
Content-Type: application/nwp-frame
X-NPS-Required-Features: nps.data_results=1,nps.invocation_context=1
X-NPS-Invocation-Context: <canonical unpadded base64url NIP envelope>
```

The body is the ordinary result ActionFrame. Native `invocation_context` and
`required_features` body carriers are invalid on this HTTP leg. Success returns the
existing capsule with the exact selected-feature echo. Rejections contain no echo.
Direct mode remains a separate binding and rejects projected callers.

If a host preserves canonical result-denial codes, record the local preflight error
in private per-request state only inside this verified-policy callback. Translate it
only when the verifier reports that policy denial (`NIP-CONTEXT-BINDING-MISMATCH`),
and emit minimized resource-denial audit. Never override signature/channel failures,
unavailable identity/resource audit, key/replay storage failures or other errors.
Do not populate this state from headers/body or persist it across requests. The real
loopback integration demonstrates this mapping; the binding does not install it.

## Request and signature semantics

`NipInvocationJson.ResultActionRequest` reads original JSON, preserves field presence,
removes only the registered correlation/carrier/outer-bound fields, and binds params,
priority, async, callback and timeout when present. Target comes from receiver routing.
Never build a verification digest from reserialized DTO defaults. The same logical
operation/key uses a fresh projection when retried after a lost reply.

`NipInvocationVerifier.AdmitAsync` checks envelope size/schema, authenticated channel
issuer, configured key/signature, receiver ceilings, time, request digest and policy
before claiming replay. `RevalidateAsync` accepts only the same host invocation object,
carrier and digest, rechecks live authority/time and never claims a second time.
`NipResultHttpBinding` keeps this opaque state in private HttpContext.Items; it is not
wire authority and must not be shared across requests or serialized by adapters.

This reference schema allows decimal integer tokens in ±9007199254740991, strings,
booleans, null and bounded nested objects/arrays. Fractions, exponent tokens, unsafe
integers, invalid Unicode and duplicate decoded keys reject. JCS ordering/escaping
is exact for supported values; there is no general floating-point canonicalizer or
generic query/byte-body support claim. Scope/capability arrays use sorted unsigned
UTF-8 order, separately from JCS UTF-16 object-key order. All comparisons are exact.

## Public gateway projection and revocation

Use `NipResultHttpProjector` with explicit host-owned options for a gateway's outgoing
result leg. Before invoking it, admit the public leg's exact feature requirements and
bound/validate its body. Supply the original public HttpContext and exact outgoing frame,
fixed receiver and downstream pairs. The projector removes public projection headers,
calls mandatory authentication across live key resolution, constrains exact delegation
ceilings, bounds lifetime to admitted authority and signs a fresh proof. The host retains
key ownership. Constructing the projector installs no endpoint or offer.

The Authenticate callback must bind the actual TLS/mTLS/session peer to independently
verified NIP identity, current CA revocation, capability and exact node/action scope.
`X-NWP-Agent`, a copied public IdentFrame and a projection are insufficient. The returned
`NipGatewayAuthority` is server-owned state, never a request DTO. Receiver authorization
must also recheck current original-subject and owner/source authority: a still-valid
gateway signature cannot override later CA or resource revocation.

Use `ResultHttpClient.SendFrameAsync` at a forwarding boundary to preserve original JSON
field presence and semantics. Each leg independently discovers/adopts its own exact
features. Return only the public leg's selected-feature echo. Never forward incoming
proofs, caller-selected URLs/keys or raw downstream response headers. Lost replies retain
uncertain execution; retry explicitly with a fresh projection and unchanged logical key.

The real two-hop fixture uses independently provisioned public/receiver mTLS pins,
CA-signed IdentFrames and SQLite CA status. Before every operation it rechecks owner
CA authority for its one registered source, including retained recipient grants, and
fails closed on unavailable CA. Valid owner status never silently restores a separately
withdrawn source. The library does not create a global CA subscription. See the
[gateway ADR](../../../../../docs/adr/2026-11-02-result-gateway-projection.md) for scope.

## Durable replay lifecycle

Use `SqliteNipProjectionReplayStore.OpenAsync` from NPS.NIP.Storage.Sqlite with an
absolute application-owned path and configured audience/capacity. At first installation
only, set `initializeNewFile: true`; the file must not exist. Normal reopen requires
existing valid state. Missing/corrupt/wrong-audience/wrong-capacity storage fails closed.
Do not reset a database, choose a new file per replica or auto-initialize after loss.

The default capacity is 65536 entries (maximum 1000000). Immediate write transactions
atomically check time and insert `(issuer, projection_id)` once. Entries remain until
at least `expires_at+5`; live capacity is never evicted. The persisted clock fence
rejects rollback. Storage/lock/capacity failures map to verifier unavailable. Projection
replay is independent of result-store TTL/replay and never proves business execution.

## Auditing and qualification

`NipInvocationAudit` and `ResultOperationAudit` carry minimized facts; configure an
appropriate host sink. Unverified assertions do not supply audit subject. Operation
events contain a hash of the locator, never rows, counts, field lists, raw locator,
envelope/signature, prompts or credentials. Do not log an entire admitted Claims object.
Configured audit failure fails closed; completed allocation can still remain retained.

Run `NipInvocationJsonTests`, `NipInvocationVerifierTests`, `NipProjectionReplayStoreTests`
and `ResultInvocationTlsTests`. The latter exercises actual loopback mTLS, signed
projections, file replay, supported result client and two-hop gateway/result actions.
Its ephemeral provisioned pins and live SQLite CA checks qualify that explicit fixture
topology only. General public PKI, generic foreign-protocol gateway migration, native
binding, multi-host failure and six-SDK parity remain separate obligations; missing
evidence is not a successful runtime claim. Projector tests additionally exercise exact
ceilings, authority/key withdrawal, schema bounds and cancellation.

See [binding ADR](../../../../../docs/adr/2026-11-02-result-invocation-http-binding.md),
[data-results profile](../../../../../spec/profiles/data-results.md) and
[D20-03](https://github.com/labacacia/NPS-Dev/issues/167).
