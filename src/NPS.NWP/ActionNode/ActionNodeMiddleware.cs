// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.Nwm;

namespace NPS.NWP.ActionNode;

/// <summary>
/// ASP.NET Core middleware exposing a single Action Node at a configurable path prefix
/// (NPS-2 §3.2, §7). Sub-paths: <c>/.nwm</c>, <c>/.schema</c>, <c>/actions</c>, <c>/invoke</c>.
/// Built-in reserved actions <c>system.task.status</c> and <c>system.task.cancel</c> are
/// handled by the middleware and MUST NOT appear in <see cref="ActionNodeOptions.Actions"/>.
/// </summary>
public sealed class ActionNodeMiddleware
{
    /// <summary>Reserved action id for polling async task state (NPS-2 §7.3).</summary>
    public const string SystemTaskStatus = "system.task.status";
    /// <summary>Reserved action id for cancelling a running async task (NPS-2 §7.3).</summary>
    public const string SystemTaskCancel = "system.task.cancel";

    private readonly RequestDelegate _next;
    private readonly IActionNodeProvider _provider;
    private readonly ActionNodeOptions _options;
    private readonly IActionTaskStore _taskStore;
    private readonly IActionTaskCancellationRegistry _taskCancellations;
    private readonly IIdempotencyCache _idempotency;
    private readonly ILogger _logger;

    private readonly string _nwmJson;
    private readonly string _actionsJson;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Clock override, primarily for tests. Defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public ActionNodeMiddleware(
        RequestDelegate next,
        IActionNodeProvider provider,
        ActionNodeOptions options,
        IActionTaskStore taskStore,
        IActionTaskCancellationRegistry taskCancellations,
        IIdempotencyCache idempotency,
        ILogger<ActionNodeMiddleware> logger)
    {
        _next = next;
        _provider = provider;
        _options = options;
        _taskStore = taskStore;
        _taskCancellations = taskCancellations;
        _idempotency = idempotency;
        _logger = logger;

        if (_options.Actions.ContainsKey(SystemTaskStatus) ||
            _options.Actions.ContainsKey(SystemTaskCancel))
        {
            throw new InvalidOperationException(
                $"Reserved action ids '{SystemTaskStatus}' / '{SystemTaskCancel}' are provided by the middleware and MUST NOT be registered in ActionNodeOptions.Actions.");
        }

        (_nwmJson, _actionsJson) = BuildStaticPayloads(_options);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var prefix = _options.PathPrefix.TrimEnd('/');

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var sub = path[prefix.Length..];

        if (_options.RequireAuth && !ctx.Request.Headers.ContainsKey(NwpHttpHeaders.Agent))
        {
            await WriteError(ctx, 401, "NPS-CLIENT-UNAUTHORIZED",
                NwpErrorCodes.AuthNidScopeViolation,
                "X-NWP-Agent header is required.");
            return;
        }

        switch (sub)
        {
            case "/.nwm":
            case "/.nwm/":
                await HandleNwm(ctx);
                break;

            case "/.schema":
            case "/.schema/":
                await HandleSchema(ctx);
                break;

            case "/actions":
            case "/actions/":
                await HandleActions(ctx);
                break;

            case "/invoke":
            case "/invoke/":
                if (ctx.Request.Method != HttpMethods.Post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }
                await HandleInvoke(ctx);
                break;

            default:
                await _next(ctx);
                break;
        }
    }

    // ── Static endpoints ─────────────────────────────────────────────────────

    private async Task HandleNwm(HttpContext ctx)
    {
        var json = _nwmJson;
        if (_options.ManifestSource is { } source)
        {
            ActionNodeManifestSnapshot snapshot;
            try { snapshot = source.Read(_options.NodeId); }
            catch (ActionNodeException error)
            {
                await WriteError(ctx, error.HttpStatus, error.NpsStatus, error.ErrorCode, error.Message);
                return;
            }
            var version = snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-NWM-Version"] = version;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == version)
            {
                ctx.Response.StatusCode = 304;
                return;
            }
            json = snapshot.Json;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeManifest;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        await ctx.Response.WriteAsync(json);
    }

    private Task HandleSchema(HttpContext ctx) => HandleActions(ctx);

    private async Task HandleActions(HttpContext ctx)
    {
        // Action Node does not own a row schema, but we still answer the conventional
        // /.schema route with the JSON action registry so tools can introspect.
        var json = _actionsJson;
        if (_options.ManifestSource is { } source)
        {
            try { json = JsonSerializer.Serialize(new { actions = source.Read(_options.NodeId).Manifest.Actions }, Json); }
            catch (ActionNodeException error)
            {
                await WriteError(ctx, error.HttpStatus, error.NpsStatus, error.ErrorCode, error.Message);
                return;
            }
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(json);
    }

    // ── /invoke ──────────────────────────────────────────────────────────────

    private async Task HandleInvoke(HttpContext ctx)
    {
        var parentContext = ExtractTraceContext(ctx);

        ActionFrame frame;
        ulong wireInputBytes;
        JsonElement rawRequest;
        try
        {
            (frame, wireInputBytes, rawRequest) = await ReadFrame<ActionFrame>(ctx);
        }
        catch (ActionNodeException ex)
        {
            await WriteError(ctx, ex.HttpStatus, ex.NpsStatus, ex.ErrorCode, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            NwpTelemetry.FrameErrors.Add(1, new TagList { { "frame_type", "action" }, { "error_code", NwpErrorCodes.ActionParamsInvalid } });
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid, ex.Message);
            return;
        }

        // Reserved actions are handled by the middleware itself (no span needed).
        if (frame.ActionId == SystemTaskStatus)
        {
            await HandleSystemTaskStatus(ctx, frame);
            return;
        }
        if (frame.ActionId == SystemTaskCancel)
        {
            await HandleSystemTaskCancel(ctx, frame);
            return;
        }

        using var activity = NwpTelemetry.Source.StartActivity(
            "nps.nwp.action.invoke", ActivityKind.Server, parentContext);
        activity?.SetTag("nps.frame.type", "action");
        activity?.SetTag("nps.action.id", frame.ActionId);
        activity?.SetTag("nps.agent.nid", ctx.Request.Headers[NwpHttpHeaders.Agent].ToString());
        activity?.SetTag("nps.action.async", frame.Async);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Look up action spec
        if (!_options.Actions.TryGetValue(frame.ActionId, out var spec))
        {
            var error = _provider.GetUnknownActionError(frame, ctx)
                ?? new ActionNodeException(404, "NPS-CLIENT-NOT-FOUND", NwpErrorCodes.ActionNotFound,
                    $"Unknown action_id '{frame.ActionId}'.");
            NwpTelemetry.FrameErrors.Add(1, new TagList { { "frame_type", "action" }, { "error_code", error.ErrorCode } });
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            await WriteError(ctx, error.HttpStatus, error.NpsStatus, error.ErrorCode, error.Message);
            return;
        }

        // Priority validation
        if (frame.Priority is not null &&
            frame.Priority is not "low" and not "normal" and not "high")
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"priority '{frame.Priority}' is invalid (allowed: low/normal/high).");
            return;
        }

        // Async-capable check
        if (frame.Async && !spec.Async)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"action '{frame.ActionId}' does not support async execution.");
            return;
        }

        // Timeout clamping
        var effectiveTimeout = ClampTimeout(frame.TimeoutMs, spec);

        // Callback URL SSRF guard (only if provided)
        if (frame.CallbackUrl is not null)
        {
            var err = ActionCallbackValidator.Validate(frame.CallbackUrl, _options.RejectPrivateCallbackUrls);
            if (err is not null)
            {
                await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                    NwpErrorCodes.ActionParamsInvalid, err);
                return;
            }
        }

        // Agent / request context. Authorization runs before cached replay so a
        // revoked principal cannot recover a previously authorized result.
        var agentNid = ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var a)
            ? a.ToString() : null;
        var priority = frame.Priority ?? "normal";
        var actionContext = new ActionContext
        {
            HttpContext = ctx,
            HttpRequestBody = rawRequest,
            AgentNid = agentNid,
            RequestId = frame.RequestId,
            TaskId = null,
            Spec = spec,
            TimeoutMs = effectiveTimeout,
            Priority = priority,
            WireInputBytes = wireInputBytes,
        };
        try
        {
            await _provider.AuthorizeAsync(frame, actionContext, ctx.RequestAborted);
        }
        catch (ActionNodeException ex)
        {
            await WriteError(ctx, ex.HttpStatus, ex.NpsStatus, ex.ErrorCode, ex.Message);
            return;
        }

        // Idempotency check (sync + async). §7.1. Scope generic action replay to
        // the authenticated caller; deployment-specific scope remains the
        // provider/store's responsibility.
        var cacheActionId = ScopedCacheActionId(_options.NodeId, frame.ActionId, agentNid);
        var paramsHash = HashParams(frame.Params);
        if (!_provider.ManagesIdempotency && frame.IdempotencyKey is not null)
        {
            var cached = _idempotency.Get(cacheActionId, frame.IdempotencyKey);
            if (cached is not null)
            {
                if (cached.ParamsHash != paramsHash)
                {
                    await WriteError(ctx, 409, "NPS-CLIENT-CONFLICT",
                        NwpErrorCodes.ActionIdempotencyConflict,
                        "idempotency_key reuse with different params.");
                    return;
                }

                if (cached.TaskId is not null)
                {
                    // async re-hit — return the original task handle
                    await WriteAsyncResponse(ctx, cached.TaskId, _taskStore.Get(cached.TaskId)?.Status ?? "pending",
                        frame.RequestId, estimatedMs: null);
                    return;
                }

                if (cached.StreamFrames is not null)
                {
                    await WriteStream(ctx, AsAsyncEnumerable(cached.StreamFrames),
                        frame.RequestId, ctx.RequestAborted);
                    return;
                }

                await WriteCaps(ctx, cached.Result, cached.AnchorRef, frame.RequestId);
                return;
            }
        }

        if (frame.Async)
        {
            var taskId = Guid.NewGuid().ToString("N");
            _taskStore.Create(taskId, frame.ActionId, frame.RequestId, agentNid);

            // Cache the task id so repeated idempotent async calls return the same handle.
            if (!_provider.ManagesIdempotency && frame.IdempotencyKey is not null)
            {
                _idempotency.TryStore(cacheActionId, frame.IdempotencyKey, new IdempotentEntry
                {
                    ActionId = frame.ActionId,
                    ParamsHash = paramsHash,
                    TaskId = taskId,
                    ExpiresAt = Clock().Add(_options.IdempotencyTtl),
                });
            }

            var runCtx = new ActionContext
            {
                AgentNid = agentNid,
                RequestId = frame.RequestId,
                TaskId = taskId,
                Spec = spec,
                TimeoutMs = effectiveTimeout,
                Priority = priority,
                WireInputBytes = wireInputBytes,
            };

            // Fire-and-forget; lifetime tied to the task (not the request).
            var taskCancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(effectiveTimeout));
            if (!_taskCancellations.TryRegister(taskId, taskCancellation))
            {
                taskCancellation.Dispose();
                throw new InvalidOperationException($"Task cancellation id collision: {taskId}");
            }
            _ = Task.Run(() => RunAsyncTask(frame, runCtx, taskCancellation));

            sw.Stop();
            activity?.SetTag("nps.task.id", taskId);
            activity?.SetStatus(ActivityStatusCode.Ok);
            NwpTelemetry.FramesProcessed.Add(1, new TagList { { "frame_type", "action" }, { "result", "async_accepted" } });
            NwpTelemetry.FrameDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "frame_type", "action" } });

            await WriteAsyncResponse(ctx, taskId, "pending", frame.RequestId,
                estimatedMs: spec.TimeoutMsDefault);
            return;
        }

        // Synchronous path
        var syncCtx = actionContext;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeout));

        ActionExecutionResult result;
        try
        {
            result = await _provider.ExecuteAsync(frame, syncCtx, cts.Token);
        }
        catch (OperationCanceledException)
        {
            NwpTelemetry.FrameErrors.Add(1, new TagList { { "frame_type", "action" }, { "error_code", "NWP-NODE-UNAVAILABLE" } });
            activity?.SetStatus(ActivityStatusCode.Error, "action execution timed out");
            await WriteError(ctx, 504, "NPS-SERVER-TIMEOUT", "NWP-NODE-UNAVAILABLE",
                "action execution timed out.");
            return;
        }
        catch (ActionNodeException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await WriteError(ctx, ex.HttpStatus, ex.NpsStatus, ex.ErrorCode, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action {ActionId} failed", frame.ActionId);
            NwpTelemetry.FrameErrors.Add(1, new TagList { { "frame_type", "action" }, { "error_code", "NWP-NODE-UNAVAILABLE" } });
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await WriteError(ctx, 500, "NPS-SERVER-INTERNAL", "NWP-NODE-UNAVAILABLE",
                "action execution failed.");
            return;
        }

        if (result.StreamFrames is not null)
        {
            var completed = await WriteStream(
                ctx, result.StreamFrames, frame.RequestId, cts.Token);
            if (!_provider.ManagesIdempotency && completed is not null && frame.IdempotencyKey is not null)
            {
                _idempotency.TryStore(cacheActionId, frame.IdempotencyKey, new IdempotentEntry
                {
                    ActionId = frame.ActionId,
                    ParamsHash = paramsHash,
                    StreamFrames = completed,
                    AnchorRef = result.AnchorRef ?? spec.ResultAnchor,
                    ExpiresAt = Clock().Add(_options.IdempotencyTtl),
                });
            }

            sw.Stop();
            activity?.SetStatus(completed is null
                ? ActivityStatusCode.Error
                : ActivityStatusCode.Ok);
            NwpTelemetry.FramesProcessed.Add(1, new TagList
            {
                { "frame_type", "action" },
                { "result", completed is null ? "stream_failed" : "stream_success" },
            });
            NwpTelemetry.FrameDurationMs.Record(sw.Elapsed.TotalMilliseconds,
                new TagList { { "frame_type", "action" } });
            return;
        }

        if (!_provider.ManagesIdempotency && frame.IdempotencyKey is not null)
        {
            _idempotency.TryStore(cacheActionId, frame.IdempotencyKey, new IdempotentEntry
            {
                ActionId = frame.ActionId,
                ParamsHash = paramsHash,
                Result = result.Result,
                AnchorRef = result.AnchorRef ?? spec.ResultAnchor,
                ExpiresAt = Clock().Add(_options.IdempotencyTtl),
            });
        }

        sw.Stop();
        activity?.SetStatus(ActivityStatusCode.Ok);
        NwpTelemetry.FramesProcessed.Add(1, new TagList { { "frame_type", "action" }, { "result", "success" } });
        NwpTelemetry.FrameDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "frame_type", "action" } });
        if (result.TokenEst > 0)
            NwpTelemetry.CgnConsumed.Add(result.TokenEst, new TagList { { "frame_type", "action" } });

        if (result.SelectedFeatures is not null)
            ctx.Response.Headers["X-NPS-Selected-Features"] = result.SelectedFeatures;
        await WriteCaps(ctx, result.Result, result.AnchorRef ?? spec.ResultAnchor,
            frame.RequestId, result.TokenEst);
    }

    private async Task<IReadOnlyList<StreamFrame>?> WriteStream(
        HttpContext ctx,
        IAsyncEnumerable<StreamFrame> source,
        string? requestId,
        CancellationToken ct)
    {
        var streamId = Guid.NewGuid().ToString("N");
        var emitted = new List<StreamFrame>();
        uint nextSeq = 0;
        var terminal = false;

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-ndjson";
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        if (requestId is not null)
            ctx.Response.Headers["X-NWP-Request-Id"] = requestId;

        try
        {
            await foreach (var supplied in source.WithCancellation(ct))
            {
                if (terminal)
                    throw ActionNodeException.Internal(
                        "Action stream emitted frames after its terminal frame.");
                if (supplied.Seq != nextSeq)
                    throw ActionNodeException.Internal(
                        $"Action stream sequence must be contiguous from zero; expected {nextSeq}, got {supplied.Seq}.");
                if (supplied.ErrorCode is not null && !supplied.IsLast)
                    throw ActionNodeException.Internal(
                        "Action stream error_code is valid only on a terminal frame.");

                var frame = supplied with { StreamId = streamId };
                emitted.Add(frame);
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(frame, Json) + "\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                nextSeq++;
                terminal = frame.IsLast;
            }

            if (!terminal)
                throw ActionNodeException.Internal(
                    "Action stream ended without a terminal frame.");
            return emitted[^1].ErrorCode is null ? emitted : null;
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            var errorCode = ex is ActionNodeException actionError
                ? actionError.ErrorCode
                : NwpErrorCodes.NodeUnavailable;
            _logger.LogWarning(ex, "Action stream {StreamId} terminated abnormally", streamId);
            if (!ctx.RequestAborted.IsCancellationRequested && !terminal)
            {
                var error = new StreamFrame
                {
                    StreamId = streamId,
                    Seq = nextSeq,
                    IsLast = true,
                    ErrorCode = errorCode,
                    Data = Array.Empty<JsonElement>(),
                };
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(error, Json) + "\n");
                await ctx.Response.Body.FlushAsync();
            }
            return null;
        }
    }

    private static async IAsyncEnumerable<StreamFrame> AsAsyncEnumerable(
        IEnumerable<StreamFrame> frames)
    {
        foreach (var frame in frames)
            yield return frame;
        await Task.CompletedTask;
    }

    private async Task RunAsyncTask(
        ActionFrame frame,
        ActionContext runCtx,
        CancellationTokenSource cancellation)
    {
        // Mark as running (only if still pending)
        _taskStore.TryTransition(runCtx.TaskId!, "pending", "running");

        try
        {
            var res = await _provider.ExecuteAsync(frame, runCtx, cancellation.Token);
            _taskStore.Complete(runCtx.TaskId!, res.Result);
        }
        catch (OperationCanceledException)
        {
            var err = JsonSerializer.SerializeToElement(
                new { code = "NWP-NODE-UNAVAILABLE", message = "task timed out" }, Json);
            _taskStore.Fail(runCtx.TaskId!, err);
        }
        catch (ActionNodeException ex)
        {
            var err = JsonSerializer.SerializeToElement(
                new { code = ex.ErrorCode, message = ex.Message }, Json);
            _taskStore.Fail(runCtx.TaskId!, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Async action {ActionId} (task {TaskId}) failed",
                frame.ActionId, runCtx.TaskId);
            var err = JsonSerializer.SerializeToElement(
                new { code = "NWP-NODE-UNAVAILABLE", message = ex.Message }, Json);
            _taskStore.Fail(runCtx.TaskId!, err);
        }
        finally
        {
            _taskCancellations.Remove(runCtx.TaskId!);
            cancellation.Dispose();
        }
    }

    // ── system.task.status / system.task.cancel ──────────────────────────────

    private async Task HandleSystemTaskStatus(HttpContext ctx, ActionFrame frame)
    {
        var taskId = ReadStringParam(frame.Params, "task_id");
        if (string.IsNullOrEmpty(taskId))
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid, "params.task_id is required.");
            return;
        }

        var rec = _taskStore.Get(taskId);
        if (rec is null)
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                NwpErrorCodes.TaskNotFound, $"Unknown task_id '{taskId}'.");
            return;
        }
        if (!OwnsTask(ctx, rec))
        {
            await WriteError(ctx, 403, "NPS-AUTH-FORBIDDEN",
                NwpErrorCodes.AuthNidScopeViolation,
                "The caller does not own this task.");
            return;
        }

        var status = new ActionTaskStatus
        {
            TaskId = rec.TaskId,
            Status = rec.Status,
            Progress = rec.Progress,
            CreatedAt = rec.CreatedAt.ToString("O"),
            UpdatedAt = rec.UpdatedAt.ToString("O"),
            RequestId = rec.RequestId,
            Result = rec.Result,
            Error = rec.Error,
        };

        var payload = JsonSerializer.SerializeToElement(status, Json);
        await WriteCaps(ctx, payload, anchorRef: null, frame.RequestId);
    }

    private async Task HandleSystemTaskCancel(HttpContext ctx, ActionFrame frame)
    {
        var taskId = ReadStringParam(frame.Params, "task_id");
        if (string.IsNullOrEmpty(taskId))
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid, "params.task_id is required.");
            return;
        }

        var rec = _taskStore.Get(taskId);
        if (rec is null)
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                NwpErrorCodes.TaskNotFound, $"Unknown task_id '{taskId}'.");
            return;
        }
        if (!OwnsTask(ctx, rec))
        {
            await WriteError(ctx, 403, "NPS-AUTH-FORBIDDEN",
                NwpErrorCodes.AuthNidScopeViolation,
                "The caller does not own this task.");
            return;
        }

        if (rec.Status is "completed" or "failed" or "cancelled")
        {
            await WriteError(ctx, 409, "NPS-CLIENT-CONFLICT",
                NwpErrorCodes.TaskAlreadyCancelled,
                $"Task '{taskId}' is already in a terminal state ('{rec.Status}').");
            return;
        }

        _taskStore.Cancel(taskId);
        _taskCancellations.Cancel(taskId);
        var payload = JsonSerializer.SerializeToElement(
            new { task_id = taskId, status = "cancelled" }, Json);
        await WriteCaps(ctx, payload, anchorRef: null, frame.RequestId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActivityContext ExtractTraceContext(HttpContext ctx)
    {
        var traceparent = ctx.Request.Headers["traceparent"].ToString();
        if (string.IsNullOrEmpty(traceparent)) return default;
        var tracestate = ctx.Request.Headers["tracestate"].ToString();
        ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var context);
        return context;
    }

    private uint ClampTimeout(uint requested, ActionSpec spec)
    {
        // Effective max = min(spec.TimeoutMsMax, options.MaxTimeoutMs)
        var specMax = spec.TimeoutMsMax ?? _options.MaxTimeoutMs;
        var hardMax = Math.Min(specMax, _options.MaxTimeoutMs);

        if (requested == 0)
        {
            return spec.TimeoutMsDefault ?? _options.DefaultTimeoutMs;
        }
        return requested > hardMax ? hardMax : requested;
    }

    private static string HashParams(JsonElement? p)
    {
        var json = p is null ? "null" : JsonSerializer.Serialize(p, Json);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private static string ScopedCacheActionId(string nodeId, string actionId, string? agentNid) =>
        $"{nodeId}\u001f{actionId}\u001f{agentNid ?? string.Empty}";

    private static bool OwnsTask(HttpContext ctx, ActionTaskRecord task)
    {
        var caller = ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var value)
            ? value.ToString()
            : null;
        return string.Equals(task.AgentNid, caller, StringComparison.Ordinal);
    }

    private static string? ReadStringParam(JsonElement? p, string name)
    {
        if (p is not { ValueKind: JsonValueKind.Object } root) return null;
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private async Task<(T frame, ulong wireInputBytes, JsonElement rawRequest)> ReadFrame<T>(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) != 0)
        {
            if (_provider.MaxRequestBytes is { } max && ms.Length + read > max)
                throw new ActionNodeException(413, "NPS-LIMIT-PAYLOAD",
                    "NWP-HTTP-BODY-TOO-LARGE", "Action request exceeds the configured limit.");
            ms.Write(buffer, 0, read);
        }
        ms.Position = 0;
        var raw = ms.ToArray();
        var frame = JsonSerializer.Deserialize<T>(raw, Json)
            ?? throw new InvalidOperationException("Failed to deserialize frame.");
        using var document = JsonDocument.Parse(raw);
        return (frame, (ulong)raw.LongLength, document.RootElement.Clone());
    }

    private Task WriteAsyncResponse(
        HttpContext ctx, string taskId, string status,
        string? requestId, uint? estimatedMs)
    {
        var body = new AsyncActionResponse
        {
            TaskId = taskId,
            Status = status,
            PollUrl = $"{_options.PathPrefix.TrimEnd('/')}/invoke",
            EstimatedMs = estimatedMs,
            RequestId = requestId,
        };
        ctx.Response.StatusCode = 202;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }

    private Task WriteCaps(
        HttpContext ctx, JsonElement? payload, string? anchorRef,
        string? requestId, uint tokenEst = 0)
    {
        var dataList = payload is null
            ? Array.Empty<JsonElement>()
            : new[] { payload.Value };

        var caps = new CapsFrame
        {
            AnchorRef = anchorRef ?? string.Empty,
            Count = (uint)dataList.Length,
            Data = dataList,
            TokenEst = tokenEst,
        };

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        if (anchorRef is not null)
            ctx.Response.Headers[NwpHttpHeaders.Schema] = anchorRef;
        if (tokenEst > 0)
            ctx.Response.Headers[NwpHttpHeaders.Tokens] = tokenEst.ToString();
        if (requestId is not null)
            ctx.Response.Headers["X-NWP-Request-Id"] = requestId;

        return ctx.Response.WriteAsync(JsonSerializer.Serialize(caps, Json));
    }

    private Task WriteError(HttpContext ctx, int status, string npsStatus, string errorCode, string message)
    {
        var err = new ErrorFrame
        {
            Status = npsStatus,
            Error = errorCode,
            Message = message,
        };
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = _provider.ErrorMediaType;
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(err, Json));
    }

    private static (string nwmJson, string actionsJson) BuildStaticPayloads(ActionNodeOptions opt)
    {
        var baseUrl = opt.PathPrefix.TrimEnd('/');

        var nwm = new NeuralWebManifest
        {
            Nwp = "0.4",
            NodeId = opt.NodeId,
            NodeType = "action",
            DisplayName = opt.DisplayName,
            Actions = opt.Actions,
            WireFormats = ["ncp-capsule", "json"],
            PreferredFormat = "json",
            Capabilities = new NodeCapabilities
            {
                Query = false,
                Stream = opt.LlmProfile?.SupportsStream ?? false,
                TokenBudgetHint = true,
            },
            Auth = new NodeAuth
            {
                Required = opt.RequireAuth,
                IdentityType = opt.RequireAuth ? "nip-cert" : "none",
            },
            Endpoints = new NodeEndpoints
            {
                Invoke = $"{baseUrl}/invoke",
                Schema = $"{baseUrl}/.schema",
            },
            Profiles = opt.LlmProfile is null
                ? null
                : new NwmProfiles { Llm = opt.LlmProfile },
        };
        var nwmJson = JsonSerializer.Serialize(nwm, Json);

        // /actions: `{ "actions": { "orders.create": { ... }, ... } }` — exposes the
        // declared action registry so Agents can introspect without re-reading the NWM.
        var actionsJson = JsonSerializer.Serialize(
            new { actions = opt.Actions }, Json);

        return (nwmJson, actionsJson);
    }
}
