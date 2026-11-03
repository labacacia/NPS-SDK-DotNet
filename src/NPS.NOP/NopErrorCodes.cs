// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP;

/// <summary>
/// NOP protocol error codes and their NPS status code mappings (NPS-5 §7).
/// </summary>
public static class NopErrorCodes
{
    /// <summary>Alpha.20 profile error. Maps to NPS-SERVER-INTERNAL; never implies safe retry.</summary>
    public const string ExecutionIndeterminate = "NOP-EXECUTION-INDETERMINATE";
    public const string TaskNotFound = "NOP-TASK-NOT-FOUND";
    public const string TaskTimeout = "NOP-TASK-TIMEOUT";
    public const string TaskDagInvalid = "NOP-TASK-DAG-INVALID";
    public const string TaskDagCycle = "NOP-TASK-DAG-CYCLE";
    public const string TaskDagTooLarge = "NOP-TASK-DAG-TOO-LARGE";
    public const string TaskAlreadyCompleted = "NOP-TASK-ALREADY-COMPLETED";
    public const string TaskCancelled = "NOP-TASK-CANCELLED";
    public const string DelegateScopeViolation = "NOP-DELEGATE-SCOPE-VIOLATION";
    public const string DelegateRejected = "NOP-DELEGATE-REJECTED";
    public const string DelegateChainTooDeep = "NOP-DELEGATE-CHAIN-TOO-DEEP";
    public const string DelegateTimeout = "NOP-DELEGATE-TIMEOUT";
    public const string SyncTimeout = "NOP-SYNC-TIMEOUT";
    public const string SyncDependencyFailed = "NOP-SYNC-DEPENDENCY-FAILED";
    public const string StreamSeqGap = "NOP-STREAM-SEQ-GAP";
    public const string StreamNidMismatch = "NOP-STREAM-NID-MISMATCH";
    public const string StreamNak = "NOP-STREAM-NAK";
    public const string ResourceInsufficient = "NOP-RESOURCE-INSUFFICIENT";
    public const string ConditionEvalError = "NOP-CONDITION-EVAL-ERROR";
    public const string InputMappingError = "NOP-INPUT-MAPPING-ERROR";
    public const string CompensationFailed = "NOP-COMPENSATION-FAILED";
    public const string CompensationPartialFailed = "NOP-COMPENSATION-PARTIAL-FAILED";
    public const string CompensationNotSupported = "NOP-COMPENSATION-NOT-SUPPORTED";
    public const string CallbackHmacMissing = "NOP-CALLBACK-HMAC-MISSING";
    public const string CallbackInvalid = "NOP-CALLBACK-INVALID";
    public const string CallbackHmacInvalid = "NOP-CALLBACK-HMAC-INVALID";

    // ── NOP v0.7 ──────────────────────────────────────────────────────────────
    /// <summary>Task result read after result_ttl_seconds elapsed → NPS-CLIENT-NOT-FOUND.</summary>
    public const string TaskResultExpired = "NOP-TASK-RESULT-EXPIRED";
    /// <summary>NAK references a frame evicted from the resend window → NPS-STREAM-SEQ-GAP.</summary>
    public const string StreamNakUnresolvable = "NOP-STREAM-NAK-UNRESOLVABLE";

    // ── NPS-CR-0007 L3 runtime (§8) ──────────────────────────────────────────
    /// <summary>TaskFrame already leased by a live runner lease → NPS-CLIENT-CONFLICT.</summary>
    public const string ClaimConflict = "NOP-CLAIM-CONFLICT";
    /// <summary>spawn_spec_ref failed to resolve to a valid SpawnSpec → NPS-CLIENT-BAD-PARAM.</summary>
    public const string SpawnSpecInvalid = "NOP-SPAWN-SPEC-INVALID";
    /// <summary>Worker exceeded the idle timeout → NPS-SERVER-TIMEOUT.</summary>
    public const string RuntimeIdleTimeout = "NOP-RUNTIME-IDLE-TIMEOUT";
    /// <summary>Worker exceeded the max runtime → NPS-SERVER-TIMEOUT.</summary>
    public const string RuntimeMaxRuntime = "NOP-RUNTIME-MAX-RUNTIME";

    // ── alpha.19 replay / aggregation hardening ──────────────────────────────
    public const string ReplayConflict = "NOP-REPLAY-CONFLICT";
    public const string ReplayLimit = "NOP-REPLAY-LIMIT";
    public const string AggregationInvalid = "NOP-AGGREGATION-INVALID";
}
