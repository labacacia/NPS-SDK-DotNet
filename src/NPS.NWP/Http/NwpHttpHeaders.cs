// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Http;

/// <summary>
/// Standard HTTP header name constants for the NWP overlay (NPS-2 §8).
/// </summary>
public static class NwpHttpHeaders
{
    // ── Request headers ──────────────────────────────────────────────────────

    /// <summary>
    /// Agent NID in <c>urn:nps:agent:{ca-host}:{id}</c> format.
    /// Required when <c>auth.required == true</c> in the node's NWM.
    /// </summary>
    public const string Agent = "X-NWP-Agent";

    /// <summary>
    /// Token budget upper limit in CGN (uint32). Node SHOULD trim the response
    /// to stay within budget, or return <c>NWP-BUDGET-EXCEEDED</c> if impossible.
    /// </summary>
    public const string Budget = "X-NWP-Budget";

    /// <summary>
    /// Caller's <see cref="NPS.NIP.Frames.IdentFrame"/> serialised as compact JSON (UTF-8).
    /// When present the Anchor Node extracts the caller's <c>assurance_level</c>
    /// for RFC-0005 §4.1.4 step 1 (<c>min_assurance_level</c> enforcement).
    /// Full cryptographic verification of the IdentFrame is out of scope for the
    /// Anchor middleware; use <c>NipIdentVerifier</c> at the connection/session layer.
    /// </summary>
    public const string Ident = "X-NWP-Ident";

    /// <summary>
    /// Comma-separated list of capability tokens the agent declares for this
    /// request (e.g. <c>"topology:read,action:invoke"</c>). Nodes with
    /// capability-gated operations check this header and reject callers that
    /// don't declare the required token.
    /// </summary>
    public const string Capabilities = "X-NWP-Capabilities";

    /// <summary>
    /// Node graph traversal depth (uint, default 1, max 5).
    /// </summary>
    public const string Depth = "X-NWP-Depth";

    /// <summary>
    /// Requested payload encoding tier: <c>"json"</c> or <c>"msgpack"</c>.
    /// Defaults to <c>"msgpack"</c> when absent.
    /// </summary>
    public const string Encoding = "X-NWP-Encoding";

    /// <summary>
    /// Agent's tokenizer identifier (e.g. <c>"cl100k_base"</c>, <c>"claude"</c>).
    /// Used for CGN → native token conversion (NPS-2 §8).
    /// </summary>
    public const string Tokenizer = "X-NWP-Tokenizer";

    // ── Response headers ─────────────────────────────────────────────────────

    /// <summary>The <c>anchor_id</c> of the schema used in the response payload.</summary>
    public const string Schema = "X-NWP-Schema";

    /// <summary>Actual CGN consumption for the response payload.</summary>
    public const string Tokens = "X-NWP-Tokens";

    /// <summary>Native token consumption (when the Agent's tokenizer is known).</summary>
    public const string TokensNative = "X-NWP-Tokens-Native";

    /// <summary>Tokenizer identifier actually used for token calculation.</summary>
    public const string TokenizerUsed = "X-NWP-Tokenizer-Used";

    /// <summary><c>"true"</c> when the response was served from the node's server-side cache.</summary>
    public const string Cached = "X-NWP-Cached";

    /// <summary>Node type of the responding server: <c>"memory"</c>, <c>"action"</c>, or <c>"complex"</c>.</summary>
    public const string NodeType = "X-NWP-Node-Type";

    /// <summary>
    /// Reputation evaluation outcome on accepted requests: <c>"clean"</c>.
    /// Set by the Anchor Node when <see cref="NwpErrorCodes.ReputationBanned"/> /
    /// rejected / throttled are NOT triggered (RFC-0005 §4.1.4 step 7).
    /// </summary>
    public const string ReputationStatus = "X-NWP-Reputation-Status";

    /// <summary>
    /// Unix timestamp (seconds) when the ban on the requester NID expires.
    /// Present only when the response error code is
    /// <see cref="NwpErrorCodes.ReputationBanned"/> (RFC-0005 §4.4).
    /// </summary>
    public const string BanExpires = "X-NWP-Ban-Expires";

    // ── MIME types ───────────────────────────────────────────────────────────

    /// <summary>MIME type for NWP request frames (<c>Content-Type</c> on requests).</summary>
    public const string MimeFrame = "application/nwp-frame";

    /// <summary>Deprecated alpha.17 compatibility alias for request frames.</summary>
    public const string MimeLegacyFrame = "application/x-nps-frame";

    /// <summary>MIME type for NWP capsule responses (<c>Content-Type</c> on responses).</summary>
    public const string MimeCapsule = "application/nwp-capsule";

    /// <summary>MIME type for NWP HTTP error responses.</summary>
    public const string MimeError = "application/nwp-error+json";

    /// <summary>MIME type for Neural Web Manifest responses.</summary>
    public const string MimeManifest = "application/nwp-manifest+json";
}

/// <summary>
/// NWP protocol error codes (NPS-2 §11).
/// </summary>
public static class NwpErrorCodes
{
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-BAD-FRAME; never implies safe retry.</summary>
    public const string ChallengeInvalid = "NWP-CHALLENGE-INVALID";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-NOT-FOUND; never implies safe retry.</summary>
    public const string ChallengeNotFound = "NWP-CHALLENGE-NOT-FOUND";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-CONFLICT; never implies safe retry.</summary>
    public const string ChallengeBindingConflict = "NWP-CHALLENGE-BINDING-CONFLICT";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-CONFLICT; never implies safe retry.</summary>
    public const string ChallengeNotApproved = "NWP-CHALLENGE-NOT-APPROVED";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-CONFLICT; never implies safe retry.</summary>
    public const string ChallengeExpired = "NWP-CHALLENGE-EXPIRED";
    /// <summary>Alpha.20 profile error. Maps to NPS-SERVER-UNAVAILABLE; never implies safe retry.</summary>
    public const string ChallengeStateUnavailable = "NWP-CHALLENGE-STATE-UNAVAILABLE";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-BAD-FRAME; never implies safe retry.</summary>
    public const string FeatureRequirementsInvalid = "NWP-FEATURE-REQUIREMENTS-INVALID";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-CONFLICT; never implies safe retry.</summary>
    public const string FeatureRequiredUnsupported = "NWP-FEATURE-REQUIRED-UNSUPPORTED";
    // Auth
    public const string AuthNidScopeViolation = "NWP-AUTH-NID-SCOPE-VIOLATION";
    public const string AuthNidExpired = "NWP-AUTH-NID-EXPIRED";
    public const string AuthNidRevoked = "NWP-AUTH-NID-REVOKED";
    public const string AuthNidUntrustedIssuer = "NWP-AUTH-NID-UNTRUSTED-ISSUER";
    public const string AuthNidCapabilityMissing = "NWP-AUTH-NID-CAPABILITY-MISSING";

    /// <summary>
    /// Agent's <c>assurance_level</c> is below the node's
    /// <c>min_assurance_level</c> (NWM §4.1) or per-action override
    /// (§4.6). Response SHOULD include a <c>hint</c> pointing to a CA
    /// enrolment URL. NPS-RFC-0003. → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string AuthAssuranceTooLow = "NWP-AUTH-ASSURANCE-TOO-LOW";

    /// <summary>
    /// Receiving Node's <c>reputation_policy</c> matched a
    /// <c>reject_on</c> rule against the requesting <c>subject_nid</c>.
    /// Reserved at NWP v0.7 (Phase 1 of NPS-RFC-0004); the policy
    /// field shape that produces this error lands at NWP v0.8 (Phase
    /// 2). → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string AuthReputationBlocked = "NWP-AUTH-REPUTATION-BLOCKED";

    // Query
    public const string QueryFilterInvalid = "NWP-QUERY-FILTER-INVALID";
    public const string QueryFieldUnknown = "NWP-QUERY-FIELD-UNKNOWN";
    public const string QueryCursorInvalid = "NWP-QUERY-CURSOR-INVALID";

    // Action
    public const string ActionNotFound = "NWP-ACTION-NOT-FOUND";
    public const string ActionParamsInvalid = "NWP-ACTION-PARAMS-INVALID";
    public const string ActionIdempotencyConflict = "NWP-ACTION-IDEMPOTENCY-CONFLICT";
    public const string LlmContextNotFound = "NWP-LLM-CONTEXT-NOT-FOUND";
    public const string LlmContextExpired = "NWP-LLM-CONTEXT-EXPIRED";
    public const string LlmContextVersionConflict = "NWP-LLM-CONTEXT-VERSION-CONFLICT";
    public const string LlmContextBindingMismatch = "NWP-LLM-CONTEXT-BINDING-MISMATCH";
    public const string LlmContextForbidden = "NWP-LLM-CONTEXT-FORBIDDEN";
    public const string LlmContextLimitExceeded = "NWP-LLM-CONTEXT-LIMIT-EXCEEDED";
    public const string LlmContextOperationUnsupported = "NWP-LLM-CONTEXT-OPERATION-UNSUPPORTED";
    public const string TaskNotFound = "NWP-TASK-NOT-FOUND";
    public const string TaskAlreadyCancelled = "NWP-TASK-ALREADY-CANCELLED";

    // Subscribe
    public const string SubscribeStreamNotFound = "NWP-SUBSCRIBE-STREAM-NOT-FOUND";
    public const string SubscribeLimitExceeded = "NWP-SUBSCRIBE-LIMIT-EXCEEDED";
    public const string SubscribeFilterUnsupported = "NWP-SUBSCRIBE-FILTER-UNSUPPORTED";
    public const string SubscribeInterrupted = "NWP-SUBSCRIBE-INTERRUPTED";
    public const string SubscribeSeqTooOld = "NWP-SUBSCRIBE-SEQ-TOO-OLD";
    public const string SubscribeLeaseInvalid = "NWP-SUBSCRIBE-LEASE-INVALID";
    public const string SubscribeLeaseExpired = "NWP-SUBSCRIBE-LEASE-EXPIRED";

    // Capacity / graph
    public const string BudgetExceeded = "NWP-BUDGET-EXCEEDED";
    public const string CgnLimitExceeded = "NWP-CGN-LIMIT-EXCEEDED";
    public const string DepthExceeded = "NWP-DEPTH-EXCEEDED";
    public const string GraphCycle = "NWP-GRAPH-CYCLE";
    public const string NodeUnavailable = "NWP-NODE-UNAVAILABLE";

    // Reputation (NPS-RFC-0005 §4.4)

    /// <summary>
    /// Request rate-limited by a <c>throttle_on</c> rule (RFC-0005 §4.4).
    /// HTTP 429. Response MUST include <c>Retry-After: 60</c>.
    /// → NPS-CLIENT-RATE-LIMITED.
    /// </summary>
    public const string ReputationThrottled = "NWP-REPUTATION-THROTTLED";

    /// <summary>
    /// Request rejected by a <c>reject_on</c> rule (RFC-0005 §4.4).
    /// HTTP 403. → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string ReputationRejected = "NWP-REPUTATION-REJECTED";

    /// <summary>
    /// Request rejected and NID temporarily banned by a <c>ban_on</c> rule
    /// or an active ban cache entry (RFC-0005 §4.4). HTTP 403.
    /// Response SHOULD include <c>X-NWP-Ban-Expires</c> (Unix timestamp).
    /// → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string ReputationBanned = "NWP-REPUTATION-BANNED";

    // Manifest
    public const string ManifestVersionUnsupported = "NWP-MANIFEST-VERSION-UNSUPPORTED";

    // HTTP binding / advertised capability
    public const string HttpOriginForbidden = "NWP-HTTP-ORIGIN-FORBIDDEN";
    public const string HttpContentTypeUnsupported = "NWP-HTTP-CONTENT-TYPE-UNSUPPORTED";
    public const string HttpAcceptUnsatisfiable = "NWP-HTTP-ACCEPT-UNSATISFIABLE";
    public const string HttpRequestIdMismatch = "NWP-HTTP-REQUEST-ID-MISMATCH";
    public const string HttpFrameBodyMalformed = "NWP-HTTP-FRAME-BODY-MALFORMED";
    public const string HttpBodyTooLarge = "NWP-HTTP-BODY-TOO-LARGE";
    public const string CapabilityAdvertisedUnimplemented = "NWP-CAPABILITY-ADVERTISED-UNIMPLEMENTED";
}
