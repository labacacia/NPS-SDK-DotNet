English | [中文](README.cn.md)

# Process-local result resources

`ResultResourceStore` implements immutable captured results, bounded reads, exact
COUNT/Int64 SUM and owner release for [data-results revision 1](../../../../../spec/profiles/data-results.md).
`ResultActionProvider` connects it to the existing HTTP Action Node `/invoke` path.
`ResultHttpClient` provides the bounded HTTPS client. Status: D20-02 COMPLETE for experimental reference AC; D20-05 demo integration IN_PROGRESS.
These APIs do not automatically advertise or qualify remote support.

## Ownership and host requirements

The host owns source consistency, authentication, NIP capability/node/action scope,
live row/field/grant policy, canonical request binding and model egress. The library
does not treat `X-NWP-Agent`, a result reference or a NIP TrustFrame as authority.
Native sessions remain unsupported. Default direct HTTP mode rejects invocation
projections. Explicit `projectedHttp: true` can use the existing NIP revision-1
[HTTP binding](../../NPS.NIP/Invocation/README.md) with authenticated channel policy,
request verification and durable replay. Its tested receiver hop does not qualify
arbitrary upstream gateways, native sessions or all SDKs. The explicit two-hop reference
integration is described in the NIP [gateway ADR](../../../../../docs/adr/2026-11-02-result-gateway-projection.md).

Construct the store with `ResultStoreOptions` and a mandatory live resource-policy
callback. Construct the provider with a finite source allowlist and a mandatory
`ResultActionVerifier`. A source has a pure `Validate` delegate and a consistent
`Capture` delegate. Validation must reject unsupported query/filter/fields/order
before source access. Capture returns already authorized finite rows in stable
order with a record-ID tie-break, source revision and SHA-256 schema reference.
The store copies the rows; the source document can then be disposed.

The verifier returns `ResultActionAdmission` containing a verified `ResultPrincipal`,
the server-computed NWP §9.7/NIP §7.8 logical request digest and a service-owned
`ToModel` decision. `ActionContext.HttpRequestBody` preserves original field presence;
do not compute a proof from reserialized ActionFrame defaults. The callback must
verify proof of possession and `nwp:action` plus exact serving node/action scope.
It runs at admission and again before execution. The store rechecks source/grant/
field/egress policy on every operation and replay, including metadata and receipts.
Invalid identities return null; unavailable policy/verifiers fail closed.

After identity verification, admission calls `ValidateResourceAccess` before dispatch:
live source/recipient/field/egress policy, existing create-key conflicts, known full
capacity, snapshot/schema/cursor bindings, expiry/release and a one-row page-size probe.
It does not capture a source, release a result, reserve capacity, advance a cursor or
claim NIP replay. Execution repeats current authorization and atomically enforces the
actual captured row/byte budget; races can still fail after admission. The page probe
materializes at most one selected row locally and returns no protected data to callers.
Unregistered actions use the profile's bounded 422 error after feature admission;
other Action Node providers retain the legacy 404 unless they explicitly opt in.

For projected requests, also call `ValidateResourceAccess` inside the NIP `Authorize`
callback, after verified claims and pure operation validation but **before** durable
replay admission. Pass only host-derived principal/tenant/revision/egress and the
verified request digest. Calling it only after `VerifyAsync` cannot guarantee that a
policy denial leaves the projection unconsumed. See the
[NIP integration instructions](../../NPS.NIP/Invocation/README.md).
The [invariant execution record](../../../../../docs/benchmarks/result-invariant-observation-2026-11-02.md)
maps all 39 frozen semantics to HTTP fixtures and states their authentication limits.
The [qualification ADR](../../../../../docs/adr/2026-11-02-reference-result-binding-qualification.md)
distinguishes the actual projected HTTP reference from unqualified direct/native paths.
Native action dispatch calls provider authorization first; this result provider returns
feature-unavailable before its native handler/verifier/capture, with no native offer.

The provider defaults to disabled. `qualifiedHttpRouteEnabled` must affirm current
local enablement and the all-backend route guarantee. Register the actions using
`ConfigureNode` and the existing `AddActionNode`/`UseActionNode` infrastructure.
Registration creates no NWM feature offer. Qualify the real verifier, authenticated
route, client admission and all backends under D20-03 before remote enablement.
The HTTP tests use a synthetic server-owned identity/digest fixture, not a qualified
NIP verifier or bridge. They are evidence for the middleware/store boundary only.

## Operations and exact supported subset

All requests use `Content-Type: application/nwp-frame`. Direct mode requires one exact
`X-NPS-Required-Features: nps.data_results=1` header. Explicit projected mode requires
`nps.data_results=1,nps.invocation_context=1` plus the single NIP projection header.
Success uses CapsFrame with
`application/nwp-capsule` and the exact `X-NPS-Selected-Features` echo. Failure uses
the registered ErrorFrame with `application/nwp-error+json` and no success echo.
Malformed, repeated, omitted, unknown, unsupported or withdrawn requirements fail
before source capture. JSON requests are bounded to 65536 bytes; duplicate decoded
keys and unknown profile parameters are rejected, rather than silently ignored.

| Action | Params / behavior |
| --- | --- |
| `result.create` | `source_id`; optional `filter`, `fields`, `order` only as validated by that source; optional `ttl_seconds`; required UUID-v4 ActionFrame `idempotency_key`. Returns descriptor only. |
| `result.read` | `result_ref`, `snapshot_id`, `schema_ref`, required `limit` (1..1000), optional `fields`, `cursor`. Returns descriptor, bounded rows, next cursor. |
| `result.aggregate` | Same three bindings plus `aggregate: {operations, group_by?}`. Mandatory `{func:"COUNT",alias:"count"}` and optional `{func:"SUM",field:"amount_minor",alias:"sum"}`. At most four group fields; names `count`/`sum` cannot be grouping fields. Other functions, aliases, COUNT-field and having reject. |
| `result.release` | `result_ref`. Owner-only idempotent receipt; removes row payload. |

Example ActionFrame JSON after authenticated admission (no new endpoint/frame):

```json
{"action_id":"result.create","idempotency_key":"6c9037fc-e3d4-4a73-91ce-fefab50af5aa","params":{"source_id":"orders"}}
```

```json
{"action_id":"result.read","params":{"result_ref":"<returned locator>","snapshot_id":"<returned snapshot>","schema_ref":"<returned schema>","limit":20,"fields":["id","reason","amount_minor"]}}
```

```json
{"action_id":"result.aggregate","params":{"result_ref":"<returned locator>","snapshot_id":"<returned snapshot>","schema_ref":"<returned schema>","aggregate":{"group_by":["reason"],"operations":[{"func":"COUNT","alias":"count"},{"func":"SUM","field":"amount_minor","alias":"sum"}]}}}
```

## Public HTTP client and explicit discovery

Construct `ResultHttpClient(baseUri, expectedIssuerNid, signer?, configureTransport?)`
with a pinned HTTPS node base path. The optional transport callback configures normal
TLS trust/mTLS credentials; redirects and cookies are forced off afterward. The client
owns and disposes its transport. A signer explicitly selects the projected leg and
receives the exact frame, pinned target and feature requirements. It must use trusted
upstream admitted identity. Without a signer, configure the direct route's authenticated
transport; no fallback to an anonymous or alternate endpoint occurs.

Every operation first GETs `/.nwm` on that route. It requires matching node/version
header, authenticated action capability, pinned invoke URL and affirmative version-1
feature offers. Discovery failures send neither a projection nor a POST. Manifest
rollback or changed content under an observed revision fails closed. Successful result
responses require the exact selected-feature echo and bounded typed payload; canonical
errors become `ResultHttpException` with `HttpStatus`, `NpsStatus`, `ErrorCode` and
`OutcomeUnknown`. No raw server bodies are exposed. There are no automatic retries or
automatic pagination. Cancellation after dispatch does not establish non-execution.

```csharp
// These inputs come from trusted host configuration, never from a model-generated URL.
using var client = new ResultHttpClient(resultNodeBaseUri, resultNodeNid,
    signAdmittedCallerRequest, configureMutualTls);
var logicalCreateKey = Guid.NewGuid(); // Persist this key before dispatch for recovery.
var result = await client.CreateAsync("orders", logicalCreateKey, ct: ct);
var summary = await client.AggregateAsync(result, ["reason"], "amount_minor", ct);
var page = await client.ReadAsync(result, 20, ["id", "reason", "amount_minor"], ct: ct);
await client.ReleaseAsync(result, ct);
```

If a create has an uncertain outcome, explicitly reuse its key and unchanged parameters
with a fresh proof within retention. Do not substitute a new key automatically. A false
`OutcomeUnknown` describes this attempt only and cannot invalidate earlier attempts.

Cancellation propagates as `OperationCanceledException`. A create canceled after its
POST may already have allocated a result, including when no reply reached the client.
Treat that attempt as uncertain and explicitly recover with its original key/parameters,
a fresh proof and current authority within retention. Cancellation does not establish
rollback; the client does not automatically retry. Actual two-hop mTLS tests stall the
reply after publication, cancel the public caller, then recover with exactly one capture.

The serving host can explicitly set `ActionNodeOptions.ManifestSource` to an
`ActionNodeManifestSource` supplying immutable complete NWM snapshots. It must persist
monotonic `ManifestVersion` across restart and atomically coordinate offer withdrawal
with route/backend enablement. `/.nwm` returns `X-NWM-Version` and supports integer
`If-None-Match`/304; action/schema discovery uses that source too. Source validation does
not prove backend qualification. No offer is created by result action registration.
See the [discovery ADR](../../../../../docs/adr/2026-11-02-result-http-discovery.md).

## Lifecycle, limits and recovery

For an implemented resource-policy option, construct `ResultResourcePolicy(issuerNid)`
and install trusted `ResultSourceAuthority` records, then pass `policy.Authorize` to
the store. All sources, delegation and model egress start denied. Authority records
must reflect current owner NIP/source policy; this class does not subscribe to a CA.
The host must update/disable them on revocation or policy change.

After owner-authenticated lookup, `store.GetOwnerGrantScope(owner, resultRef)` returns
an immutable server-local input for `policy.IssueGrant`. The host explicitly names
the verified recipient, read/aggregate operations, narrowed fields, expiry no later
than the result and optional permitted model egress. This API is not a remote action.
Recipients cannot get an owner scope, recursively grant, release an owner's result
or gain another result from the same source. A grant ID is only a management ID.
`RevokeGrant` checks its owner/tenant; source/delegation withdrawal also denies retained
recipient reads. A changed policy revision requires explicit recapture of the rows.

Policy state has a separate bounded allowance: 128 source authorities, 4096 grants
and 16 MiB logical serialized policy metadata plus fixed overhead. No live grant is
evicted; expired/revoked grants release their charge. No durable/replicated grant
guarantee is added. See the [recipient-policy ADR](../../../../../docs/adr/2026-11-02-result-recipient-policy.md).

Default TTL is 300 seconds, maximum 900. Remaining TTL cannot increase on wall-clock
rollback. Limits are 16 live results per owner, 100000 rows/32 MiB per result and
128 MiB logical retained payload/metadata/replay state. Metadata/tombstones also have
a 4096-record ceiling. Options can lower these bounds. Concurrent publication checks
all limits atomically; live results are never evicted to admit another request.
Read/aggregate output is bounded to 64 KiB before capsule wrapping/compression.
An oversized row, unsupported scalar/numeric aggregate or overflow fails explicitly.

Capture reuses row-name validation and copies UTF-8 through a per-capture owned
writer buffer, checking serialized size between rows and after array framing. The
retained payload is still an exact independent byte array. This avoids whole-dataset
staging in the shared serializer buffer pool. Logical retained-byte limits are not
a process RSS limit: host source captures, temporary copying, TLS/crypto and runtime
memory must be budgeted and measured separately.

Inspection walks the retained UTF-8 array one row at a time rather than constructing
a DOM for the whole result on each page/aggregate. Pages independently serialize selected
values, and aggregation retains scalar keys only once per bounded distinct group. The
result still stores an immutable copied payload; this is not a live-stream/delta claim.

The provider owns replay and bypasses ActionNode's generic 24-hour cache: a cached
descriptor/page must not outlive its result, policy or tenant authority. Same create
key and digest returns the existing authorized descriptor within the process TTL.
Changed digest conflicts. Released results retain bounded replay metadata until
expiry. All operations perform current resource checks; source/grant revocation can
deny retained results. This does not retract data already delivered.

On expiry, release, restart or unproven successor, access returns unavailable.
Re-query explicitly creates a new snapshot. Snapshot/schema/cursor mismatch requires
explicit rebase; never silently switch to live data. Cancellation after publication
can leave an allocated result: retry the same create key to recover an authorized
descriptor within TTL. No external business mutation, durable recovery, derived-result
operation or live subscription is implemented here.

Use bounded audit metadata only: verified subject, action, outcome and hashed locator/
snapshot identifiers. Do not log rows, full request bodies, prompts, credentials or
signing material. Optional `ResultOperationAudit` callbacks receive only the verified
tenant/subject, operation, outcome and hashed locator; configure the host sink.
Audit failure is unavailable, but can follow allocation and does not prove rollback.

## Verification and remaining qualification

Run the `ResultResourceStoreTests`, `ResultActionHttpTests` and existing
`ActionNodeMiddlewareTests` in `impl/dotnet/tests/NPS.Tests`. HTTP tests exercise
actual capsules/errors, pagination, COUNT/SUM, release, copied handles, revocation,
expiry, model egress, admission rejection and source-call counters. Existing action
tests protect default replay behavior. Full upstream NIP/delegation/bridge, native admission
and six-SDK parity remain unqualified.
`ResultInvocationTlsTests` separately exercises the integer-only projected receiver
over actual mTLS/Ed25519/file replay and supported client with explicit test-only offers.
It covers discovery withdrawal, version/redirect fences, malformed/oversized responses,
missing-echo recovery and structured errors; that evidence does not qualify production
gateways or the full suite.

The two-hop reference additionally uses `NipResultHttpProjector` with actual public
mTLS identities, CA-signed certificates and SQLite CA status. It rechecks subject and
the registered source owner's revocation before result access, including retained
recipient grants. This is a host integration pattern, not automatic production PKI
or migration of every generic bridge. Actual tests also reject recipient SUM over an
ungranted field, allow granted COUNT, then reject after revocation with zero additional
dispatch. Raw and filtered source results both deny read/aggregate after source or
owner-CA revocation. There is no new derived-result TTL or live-result-stream claim.
`SendFrameAsync` preserves exact forwarded JSON
while enforcing the same discovery/feature/error bounds; its JSON return bypasses the
typed convenience methods' descriptor consistency checks.

The [frozen oracle observation](../../../../../docs/benchmarks/result-oracle-observation-2026-11-02.md)
records the 100/1000/100000-row refund workflow through that supported public path.
Exact integer groups, bounded supporting records, provenance, snapshot conflict and
release are checked against the shared preregistered answers. This correctness evidence
does not substitute for C/D transport/provider measurements.

See [runtime cache ADR](../../../../../docs/adr/2026-11-02-result-action-replay.md),
[D20-02](https://github.com/labacacia/NPS-Dev/issues/166) and
[D20-03](https://github.com/labacacia/NPS-Dev/issues/167).
