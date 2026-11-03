// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.Nwm;

namespace NPS.NWP.ActionNode;

/// <summary>
/// Configuration for a single Action Node (NPS-2 §2.1, §7).
/// Register via <c>AddActionNode&lt;TProvider&gt;()</c>.
/// </summary>
public sealed class ActionNodeOptions
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>Node NID, e.g. <c>urn:nps:node:api.example.com:orders</c>.</summary>
    public required string NodeId { get; set; }

    /// <summary>Human-readable name shown in the NWM manifest.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Action registry. Keys are <c>{domain}.{verb}</c> identifiers (NPS-2 §4.6).
    /// The built-in <c>system.task.status</c> and <c>system.task.cancel</c> actions are
    /// provided by the middleware itself and MUST NOT be added here.
    /// </summary>
    public required IReadOnlyDictionary<string, ActionSpec> Actions { get; set; }

    // ── Routing ──────────────────────────────────────────────────────────────

    /// <summary>HTTP path prefix where the node listens, e.g. <c>"/orders"</c>.</summary>
    public required string PathPrefix { get; set; }

    // ── Auth ─────────────────────────────────────────────────────────────────

    /// <summary>When <c>true</c>, requests without <c>X-NWP-Agent</c> are rejected with 401.</summary>
    public bool RequireAuth { get; set; } = false;

    // ── Timeouts ─────────────────────────────────────────────────────────────

    /// <summary>Default timeout when neither <see cref="ActionSpec.TimeoutMsDefault"/>
    /// nor <c>ActionFrame.TimeoutMs</c> are set. Default 5000 ms.</summary>
    public uint DefaultTimeoutMs { get; set; } = 5_000;

    /// <summary>Hard cap per NPS-2 §7.1: requests above this are clamped. Default 300000 ms.</summary>
    public uint MaxTimeoutMs { get; set; } = 300_000;

    // ── Idempotency ──────────────────────────────────────────────────────────

    /// <summary>Time-to-live for idempotency cache entries. Default 24 h (NPS-2 §7.1).</summary>
    public TimeSpan IdempotencyTtl { get; set; } = TimeSpan.FromHours(24);

    // ── Async task retention ─────────────────────────────────────────────────

    /// <summary>How long a completed/failed/cancelled task remains queryable via
    /// <c>system.task.status</c>. Default 1 h.</summary>
    public TimeSpan TaskRetention { get; set; } = TimeSpan.FromHours(1);

    // ── Callback SSRF guard ──────────────────────────────────────────────────

    /// <summary>When <c>true</c>, <c>callback_url</c> is rejected if it resolves to a loopback
    /// or private IPv4 range. Default <c>true</c>.</summary>
    public bool RejectPrivateCallbackUrls { get; set; } = true;

    // ── Token budget ─────────────────────────────────────────────────────────

    /// <summary>Default token budget when <c>X-NWP-Budget</c> header is absent. 0 = unlimited.</summary>
    public uint DefaultTokenBudget { get; set; } = 0;

    // ── Optional profiles ──────────────────────────────────────────────────────

    /// <summary>
    /// Optional NWM LLM/Thinking profile. Set this when the Action Node exposes
    /// standard LLM actions such as <c>llm.complete</c>.
    /// </summary>
    public NwmLlmProfile? LlmProfile { get; set; }

    /// <summary>
    /// Optional explicit host-owned manifest source. Use only after the advertised route's
    /// bindings/all backends qualify; this does not enable a provider. The host owns persisted
    /// monotonic revisions across restart and atomically updates offers with admission policy.
    /// </summary>
    public ActionNodeManifestSource? ManifestSource { get; set; }
}
