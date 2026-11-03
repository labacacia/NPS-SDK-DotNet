English | [中文](README.cn.md)

# NCP reference transport and schema-batch receiver

NcpNativeClient/Server own native handshake and negotiated encoding policy.
NcpSession exposes the authenticated transport to upper protocols; it does not create
resource authority. The new candidate feature requires explicit local opt-in on both
ends and exact selection on an actual mutually authenticated TLS session.

The experimental [schema-batch candidate](../../../../../spec/profiles/schema-batches.md)
and [CR-0017](../../../../../spec/cr/NPS-CR-0017-schema-batches.md) define an optional
reference over existing encoded Anchor/Caps/Stream/Diff frames. SchemaBatchReceiver
is a local bounded state machine, not a feature advertisement.
Its caller-supplied FeatureSelection objects are not proof of actual admission. Hosts
must integrate and qualify negotiation, authenticated producer/source policy, frame
allocation, tier policy, serialized ingress and consumer lifetime before enabling it.

NcpNativeClientOptions provides the host-owned transport authentication hook and local
experimental enablement; NcpHandshakeProfile owns server enablement. Raw Hello and
handshake Caps validate CR-0012 bounds and exact feature selection before returning a
session. Unknown required features reject; optional unsupported features are omitted.
An old missing echo cannot satisfy a requirement. NcpSession.SelectedFeatures belongs
only to that admitted connection. CreateSchemaBatchReceiver applies its negotiated
payload/tier limits and owns one live receiver, disposed with the session. Hosts still
check live source policy and cancel/dispose on unexpected disconnect or revocation.
See the [binding ADR](../../../../../docs/adr/2026-11-02-schema-batch-session-binding.md).

Codec representation tests prove that object JsonElement rows remain MessagePack maps
with names. The new receiver accepts explicit ordered array rows; it does not change
legacy serialization or convert objects automatically. No tokenizer/provider cost
follows from this transport representation.

## Processing and failure

Construct one receiver for one host-verified producer/source/subscription. Send a
verified ordered Anchor, then a contiguous bounded full Stream. Staged chunks are
private; the final chunk publishes an immutable SchemaBatchSnapshot at version 0.
Diff.EntityId/AnchorRef/BaseSeq must match its epoch/schema/version. Supported diffs
replace existing typed cells atomically and increment version; old delivered snapshots
remain immutable. Unary Caps is unversioned and cannot establish a delta baseline.

Receive accepts exactly one complete encoded frame, validates length before decode,
rejects duplicate map members, floating tokens before normalization, excessive nesting,
binary/extension cell coercion, mismatched FINAL and malformed positional values.
Both absolute and monotonic TTL checks apply. Initial receiver credit cannot be enlarged
by a producer; only the consumer can GrantCredit after processing, then sends its
returned control on the reverse binding. Diff processing is synchronous and retains no
update queue; baseline credit is not a general subscription/transport scheduler.

Any wire fault or cancellation terminates and clears owned state. Dispose clears local
state; it cannot retract already delivered immutable values. Create a newly authorized
receiver and complete full baseline with a new epoch after reconnect or failure.
Never reconstruct an unproven snapshot by guessing missed diffs. A host must cancel/
dispose on authority withdrawal, connection loss and consumer termination.

Hard caps and supported primitive/patch subsets are SB-01..08 in the paired profile.
MaxRetainedBytes counts compact tuple JSON including outer array punctuation; it is
not process RSS or total allocations. Decoder, staging and atomic publication may
temporarily coexist; benchmarks must include their actual cost.

Retained rows are parsed from the same counted compact bytes. A raw JSON regression
first reproduced 12000 uncounted whitespace bytes surviving Clone; owning the compact
representation removes them. Arbitrary-data RFC 8785 canonicalization is not implied.

## Executed and remaining evidence

SchemaBatchStreamPublisher and SchemaBatchStreamReader connect these boundaries on a
dedicated selected session. Trusted host callbacks authorize before capture and each
write/read/delivery. The publisher bootstraps with one data frame, then waits for bounded
reverse credit; the reader drains a granted window before replenishing it. Zero pauses,
and wrong epoch/watermark, replayed, nonempty or excessive credit terminates. There is no
background application queue. Credit/frame waits are bounded and caller cancellation
still applies. Local capture is immutable and host-owned for the transfer lifetime.

PublishAsync returns local write counters, never receiver-commit proof. BaselineTupleBytes
refers to the baseline, including when a later SendDiffAsync receipt advances local version.
ReadNextAsync returns a complete immutable baseline, then one exact-base update per call.
An I/O/timeout/policy/cancellation fault closes this dedicated session and clears receiver
state; already delivered snapshots/bytes cannot be retracted and no retry is automatic.
Use a freshly authorized full baseline after loss. The actual fixture disables TLS session
resumption: resumed mutual-peer evidence is not qualified by this first reference.

45 actual encoded-frame/representation tests pass across JSON/MessagePack: complete
publication, immutable old version, wrong epoch/schema/base, duplicate/gapped chunks
and updates, atomic patch failure, field order/type/null/unsafe integer, absolute and
monotonic expiry, credit/overflow/refresh spoofing, cancellation, retained-byte boundary,
reconnect oracle, oversized/trailing/duplicate/null wire and floating-token coercion.

18 real native-session cases pass, including pinned mTLS/public APIs, both encodings,
actual reverse credit/baseline/diff, disabled/local opt-in, plaintext refusal, reconnect,
invalid raw Hello, old/malicious selection and metadata/lifetime rules. A MessagePack
revision 1.0 admission defect first failed and now rejects before application bytes.

The shared corpus adds 106 fixed JSON/MessagePack cases consumed directly by the
receiver. Omitted patch_format and explicit nullable value:null exposed typed DTO
decoding gaps; the profile preserves their raw presence without changing legacy
DTO/codec behavior. Missing value still rejects. A canonical MessagePack baseline also
failed against the old PascalCase envelope: Python/TypeScript source uses snake_case.
SchemaBatchFrameCodec and feature-bearing handshake Caps now use canonical fields in
both encodings. EncodeSchemaBatchFrame applies actual session selection, tier/payload
and lifetime. Raw token validation precedes bounded JSON projection/typed decoding;
projection allocation/CPU must be included in measurements. Runtime binary handling
uses the established MessagePack library.

Compute new profile schema IDs with SchemaBatchFrameCodec.ComputeAnchorId. It uses
the existing ordered Anchor schema shape and the strict JCS string primitive already
used for invocation binding. Ten independent Unicode/HTML/control/normalization cases
link to the unchanged 106-case wire freeze; both encodings first passed only 2/20,
then pass all 20 after strict escaping. Lone UTF-16 surrogates reject. Unselected
legacy AnchorIdComputer escaping remains unchanged. The materialized schema includes
nullable=false and omits null semantic annotations; this API is not arbitrary JSON JCS.

The [additive registry successor](../../../../../docs/adr/2026-11-02-additive-schema-batch-registration.md)
preserves the original result freeze while registering this candidate. General
NIP/source-policy integration, source-scale/resource and
equivalent compression/alternative-format measurements remain D20-04 IN_PROGRESS.
No native result operation, NWP live result stream, other SDK or release is qualified.

22 additional real transfer cases qualify the host-callback reference: both encodings at
100/1000/100000 rows with exact IDs/sums, bound updates/immutable prior state, zero credit
pause/resume, stale/spoofed/replayed credit, source/consumer denial, cancellation before
capture/while blocked, bounded missing-credit timeout, oversized rows, partial EOF and
fresh-session recovery. This is provisioned peer/source policy, not general NIP delegation.
Comparative CPU/allocation/peak and efficiency remain unmeasured for this revision.
