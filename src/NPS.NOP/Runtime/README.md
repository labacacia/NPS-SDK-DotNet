English | [中文](README.cn.md)

# Experimental bounded result-task execution

Explicit `NopOrchestratorOptions.RuntimeBinding` selects this reference path. It is
not enabled by default and does not advertise complete `nps.production_runtime`.
The normal orchestrator remains unchanged when the binding is null.

The host creates a binding per authenticated caller. `CallerNid` comes from current
admission, not a TaskFrame, model assertion or forwarded header. Its authenticated
worker adapter attests an independently verified peer identity for each AlignStream
frame. A matching `SenderNid` alone is insufficient. Result APIs independently verify
current NIP and resource policy: neither task context nor a result locator grants access.

Execution freezes the task and compares a bounded canonical digest under the atomic
`(caller_nid, task_id)` journal key. Object properties are sorted by UTF-16 ordinal
order using the shared JCS string primitive; numeric inputs are restricted to safe
integers. Other numeric forms fail instead of claiming a general JCS implementation.
Stable random UUID-v4 subtask/operation identities are persisted before dispatch.
A duplicate still running returns `FinalState=Running` with no execution_outcome;
a terminal duplicate returns the first committed result. Changed immutable input is
`NOP-REPLAY-CONFLICT`, including an invalid change to an already admitted task.
Precancellation cannot replace an existing recorded outcome.

The serial DAG reuses DagValidator/NopInputMapper. Each complete upstream input must
have authoritative terminal evidence. The adapter must bind actual peer, task/subtask,
one stream UUID, contiguous sequence from zero and final-only certainty. Missing,
contradictory, malformed or lost terminal evidence becomes nonretryable `indeterminate`.
That stops downstream work; it does not imply the effect did not happen. Positive
`not_dispatched` plus a retryable error may use the remaining retry/deadline budget
with the original operation identity. A known terminal failure is not automatically
reexecuted just because an error says retryable. Parent certainty stays separate from
completed/failed/cancelled state. No blind compensation or rollback claim is made.

The task and each node keep their original deadlines across retries. Waiting is
bounded even if a worker ignores cancellation; a late effect cannot replace the
committed terminal. Cancel requests run outside admission locks and cannot rewrite a
finished task. The host must contain/stop any noncooperative external worker; this
executor cannot undo effects or forcibly terminate an arbitrary remote process.

Reference bounds: 64 nodes, 64 KiB task, 16 frames/64 KiB observed stream per attempt,
256 KiB retained node outputs, task deadline at most120s and result TTL1–900s.
Callbacks, preflight, conditions, K-of-N and compensation actions are rejected before
dispatch in this path. Remote NOP transports, native bindings, complete production
runtime, all six SDKs and generalized compensation remain unqualified here.

The SDK journal interface isolates storage from orchestration. The existing NopDag
host sample provides `SqliteNopRuntimeJournal`: explicit initial creation, exclusive
local ownership, FULL synchronous transactions, permanent tombstones, terminal CAS,
256-task capacity with no eviction, and fail-closed reopen on missing database/schema.
It uses SQLitePCLRaw bundle3.0.5 rather than adding the affected2.1.11 native dependency;
see [maintainer package](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/)
and [advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q).
This local reference does not promise shared replicas/network filesystems, rollback-
resistant backup recovery, result migration or exactly-once external effects.
The host protects the directory/backups and preserves both database and `.owner`
marker. Never silently create new history on restart or delete it as result cleanup.

For a fresh, dedicated absolute local path only:

```csharp
using var journal = new SqliteNopRuntimeJournal(path, initializeNewFile: true);
var options = new NopOrchestratorOptions {
    RuntimeBinding = new(authenticatedCallerNid, journal, authenticatedWorkerAdapter)
};
```

On restart use `new SqliteNopRuntimeJournal(path)`; initialization against existing
history is rejected. A second live owner is rejected. After exclusive host loss,
pending tasks become `indeterminate` without redispatch; known terminal records
remain immutable. Result TTL hides payloads while tombstones remain. Clock rollback
and backup restoration across hosts do not receive a production retention guarantee.

Actual reference checks from canonical NPS-Dev:

```bash
dotnet test impl/dotnet/tests/NPS.Tests/NPS.Tests.csproj -c Release --filter 'FullyQualifiedName~NopRuntimeExecutorTests|FullyQualifiedName~NopWorkersExchange|FullyQualifiedName~NopHandoffRejects'
```

The four-node data DAG has query-orders/query-refunds -> analysis -> report. Two
host-bound worker roles use distinct Alice/Bob mTLS certificates and current NIP
identities for every gateway/receiver data hop. Dispatch between roles is local;
this is not authenticated remote AlignStream transport qualification. Alice owns
snapshots; Bob gets exact snapshot/field/aggregate grants and cannot read/release.
The report inspects five rows, then Alice revokes grants and releases known snapshots.
Expired results return the frozen uniform NOT-FOUND; an unknown query response stops
all downstream work and leaves an explicit unresolved outcome. No model is involved.

[Implementation/remaining AC](../../../../../docs/plans/2026-11-03-nop-result-handoff-implementation.md).
