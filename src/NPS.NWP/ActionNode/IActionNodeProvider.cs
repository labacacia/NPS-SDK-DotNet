// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NPS.Core;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;
using NPS.NWP.Http;

namespace NPS.NWP.ActionNode;

/// <summary>
/// Result of a single action execution.
/// </summary>
public sealed class ActionExecutionResult
{
    /// <summary>Exact feature pairs admitted by the provider; emitted only on HTTP success.</summary>
    public string? SelectedFeatures { get; init; }
    /// <summary>
    /// Action output. Serialised into <c>CapsFrame.Data</c> (single element) for the
    /// response. <c>null</c> is allowed for side-effecting actions with no return payload.
    /// </summary>
    public JsonElement? Result { get; init; }

    /// <summary>
    /// Streaming action output. When present, the Action Server writes each frame as
    /// NDJSON and does not emit a <c>CapsFrame</c>. Providers must emit one terminal
    /// frame; the server owns the externally visible stream id.
    /// </summary>
    public IAsyncEnumerable<StreamFrame>? StreamFrames { get; init; }

    /// <summary>
    /// Optional anchor_id for the response schema. Overrides <see cref="ActionSpec.ResultAnchor"/>
    /// on the NWM entry when the provider returns a polymorphic shape.
    /// </summary>
    public string? AnchorRef { get; init; }

    /// <summary>Approximate token count of the serialised result. 0 when unknown.</summary>
    public uint TokenEst { get; init; }
}

/// <summary>
/// Execution context passed to <see cref="IActionNodeProvider.ExecuteAsync"/>.
/// Carries the request metadata so providers can make scheduling and auditing decisions.
/// </summary>
public sealed class ActionContext
{
    /// <summary>
    /// Current HTTP host context, absent on native/background calls. Its User is trustworthy
    /// only after deployment authentication; request headers alone never prove identity.
    /// Providers must not retain the context beyond the request lifetime.
    /// </summary>
    public HttpContext? HttpContext { get; init; }

    /// <summary>Original parsed JSON request, preserving field presence for binding verification.</summary>
    public JsonElement? HttpRequestBody { get; init; }
    /// <summary>NID of the calling agent from <c>X-NWP-Agent</c> header, or <c>null</c>.</summary>
    public required string? AgentNid { get; init; }

    /// <summary><c>RequestId</c> echoed from <see cref="ActionFrame"/>, or <c>null</c>.</summary>
    public required string? RequestId { get; init; }

    /// <summary>Task id when executing asynchronously, <c>null</c> for sync calls.</summary>
    public string? TaskId { get; init; }

    /// <summary>Matching NWM entry for the <c>action_id</c>.</summary>
    public required ActionSpec Spec { get; init; }

    /// <summary>Effective timeout after clamping. Providers SHOULD respect it.</summary>
    public required uint TimeoutMs { get; init; }

    /// <summary>Priority hint from <see cref="ActionFrame.Priority"/>, or <c>"normal"</c>.</summary>
    public required string Priority { get; init; }

    /// <summary>
    /// Exact serialized ActionFrame payload bytes accepted at the NWP decoder boundary.
    /// Excludes NCP headers, encryption/TLS framing, and response bytes.
    /// </summary>
    public ulong WireInputBytes { get; init; }
}

/// <summary>
/// Protocol-aware failure raised by an Action Node provider. The middleware maps
/// this exception to an ErrorFrame without replacing the provider's public error.
/// </summary>
public sealed class ActionNodeException : Exception
{
    public ActionNodeException(
        int httpStatus,
        string npsStatus,
        string errorCode,
        string message)
        : base(message)
    {
        HttpStatus = httpStatus;
        NpsStatus = npsStatus;
        ErrorCode = errorCode;
    }

    public int HttpStatus { get; }
    public string NpsStatus { get; }
    public string ErrorCode { get; }

    public static ActionNodeException ParamsInvalid(string message) =>
        new(422, NpsStatusCodes.ClientUnprocessable, NwpErrorCodes.ActionParamsInvalid, message);

    public static ActionNodeException Internal(string message) =>
        new(500, NpsStatusCodes.ServerInternal, NwpErrorCodes.NodeUnavailable, message);
}

/// <summary>
/// Implement this to expose a set of actions on an Action Node. All <c>action_id</c> values
/// declared in <see cref="ActionNodeOptions.Actions"/> MUST be handled.
/// </summary>
public interface IActionNodeProvider
{
    /// <summary>
    /// True only when this provider owns all replay validation, expiry and storage.
    /// The middleware then bypasses its generic cache on every operation.
    /// </summary>
    bool ManagesIdempotency => false;

    /// <summary>Optional finite JSON request limit, enforced before deserialization.</summary>
    int? MaxRequestBytes => null;

    /// <summary>Error envelope media type; legacy providers retain their existing JSON binding.</summary>
    string ErrorMediaType => "application/json";

    /// <summary>
    /// Optional profile error for an unregistered action, before authorization or
    /// execution. Implementations may validate feature admission but must not execute
    /// actions or consume authority. Null preserves the legacy action-not-found error.
    /// </summary>
    ActionNodeException? GetUnknownActionError(ActionFrame frame, HttpContext context) => null;

    /// <summary>
    /// Authorize and validate an action before HTTP cache lookup or native execution.
    /// Both serving paths invoke this hook; an authenticated native transport alone
    /// does not grant operation/resource authority. Stateful providers re-check NIP
    /// authorization before returning a cached result.
    /// </summary>
    Task AuthorizeAsync(
        ActionFrame frame,
        ActionContext context,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Execute one action. May be invoked either synchronously (the caller awaits the
    /// result) or from the background async task runner (in which case
    /// <see cref="ActionContext.TaskId"/> is non-null).
    /// </summary>
    Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame,
        ActionContext context,
        CancellationToken ct = default);
}
