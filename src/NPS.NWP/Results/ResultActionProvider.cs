// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using NPS.Core;
using NPS.Core.Alpha20;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;

namespace NPS.NWP.Results;

/// <summary>
/// Trusted host admission, never deserialized from a request. RequestDigest must bind
/// the original logical request under NWP §9.7/NIP §7.8, including the exact feature set.
/// ToModel is a service decision, not a caller's assertion of permission.
/// </summary>
public sealed record ResultActionAdmission(ResultPrincipal Principal, string RequestDigest, bool ToModel = false);

/// <summary>Minimized result-operation audit: no rows, counts, field lists, parameters or raw locator.</summary>
public sealed record ResultOperationAudit(string Tenant, string Subject, string Operation, string Outcome, string? ResultHash);

/// <summary>
/// Deployment verifier for authenticated NIP identity, nwp:action and exact node/action
/// scope. It must bind proof of possession and the original request before returning.
/// Absence/invalid identity returns null; unavailable verification throws and fails closed.
/// ActionContext.AgentNid is never authentication evidence. Called again before execution.
/// </summary>
public delegate ValueTask<ResultActionAdmission?> ResultActionVerifier(
    ActionFrame frame, ActionContext context, CancellationToken cancellationToken);

/// <summary>
/// One deployment-owned source. Validate is pure, rejects unsupported query/filter/fields/order
/// before capture, and must not access the source or consume authority. Capture applies the
/// validated subset and live row policy with a stable record-ID tie-break in one snapshot.
/// Neither delegate may execute caller-supplied URLs, SQL or code.
/// </summary>
public sealed record ResultActionSource(
    Action<JsonElement> Validate,
    Func<ResultPrincipal, JsonElement, CancellationToken, ValueTask<ResultSnapshot>> Capture);

/// <summary>
/// Opt-in HTTP ActionFrame adapter for the process-local store. No native binding or feature
/// advertisement is registered by this class. Qualification of the real verifier, client route
/// and all backends is a host responsibility before remote enablement (D20-03).
/// </summary>
public sealed class ResultActionProvider : IActionNodeProvider
{
    /// <summary>The only exact feature set supported by this direct-identity adapter.</summary>
    public const string FeatureSelection = "nps.data_results=1";
    private readonly ResultResourceStore _store;
    private readonly IReadOnlyDictionary<string, ResultActionSource> _sources;
    private readonly ResultActionVerifier _verify;
    private readonly Func<bool> _routeEnabled;
    private readonly string _featureSelection;
    private readonly bool _projectedHttp;
    private readonly Action<ResultOperationAudit>? _audit;

    /// <summary>
    /// Create a disabled-by-default adapter. The optional route gate must affirm current
    /// local enablement and the all-backend guarantee on every request; no cached discovery.
    /// Source mappings are copied. No fallback identity/verifier/source is installed.
    /// projectedHttp selects the existing invocation-context revision-1 binding; its trusted
    /// verifier must implement complete NIP verification, request binding and durable replay.
    /// This flag alone never implements, authenticates or advertises that binding.
    /// </summary>
    public ResultActionProvider(ResultResourceStore store,
        IReadOnlyDictionary<string, ResultActionSource> sources, ResultActionVerifier verifier,
        Func<bool>? qualifiedHttpRouteEnabled = null, bool projectedHttp = false,
        Action<ResultOperationAudit>? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(verifier);
        if (sources.Count is < 1 or > 128 || sources.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || System.Text.Encoding.UTF8.GetByteCount(pair.Key) > 128
                || pair.Value is null || pair.Value.Validate is null || pair.Value.Capture is null))
            throw new ArgumentException("A finite, validated source allowlist is required.", nameof(sources));
        _store = store;
        _sources = new Dictionary<string, ResultActionSource>(sources, StringComparer.Ordinal);
        _verify = verifier;
        _routeEnabled = qualifiedHttpRouteEnabled ?? (() => false);
        _projectedHttp = projectedHttp;
        _featureSelection = projectedHttp ? FeatureSelection + ",nps.invocation_context=1" : FeatureSelection;
        _audit = audit;
    }

    /// <inheritdoc />
    public bool ManagesIdempotency => true;
    /// <inheritdoc />
    public int? MaxRequestBytes => 65536;
    /// <inheritdoc />
    public string ErrorMediaType => "application/nwp-error+json";

    /// <inheritdoc />
    public ActionNodeException? GetUnknownActionError(ActionFrame frame, HttpContext context)
    {
        try { ValidateFeatureAdmission(context); }
        catch (ActionNodeException error) { return error; }
        return Invalid();
    }

    /// <summary>Register only the four synchronous actions; this is not a feature advertisement.</summary>
    public void ConfigureNode(ActionNodeOptions options)
    {
        options.Actions = new Dictionary<string, ActionSpec>(StringComparer.Ordinal)
        {
            ["result.create"] = Spec("Capture an allowlisted immutable snapshot"),
            ["result.read"] = Spec("Inspect one bounded snapshot page"),
            ["result.aggregate"] = Spec("Compute exact COUNT and optional SUM"),
            ["result.release"] = Spec("Release an owner-bound process result"),
        };
    }

    /// <inheritdoc />
    public async Task AuthorizeAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default)
        => _ = await AdmitAsync(frame, context, ct).ConfigureAwait(false);

    /// <summary>
    /// Non-consuming resource preflight for an independently verified caller and exact
    /// request digest. Hosts can invoke this inside NIP policy admission BEFORE replay
    /// claim, after pure operation validation. This is not identity verification, an
    /// authority token, a resource reservation or execution. Admission/execution repeat
    /// current policy; a successful preflight cannot authorize later changed parameters.
    /// </summary>
    public void ValidateResourceAccess(ActionFrame frame, ResultActionAdmission admission, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.Params is not { ValueKind: JsonValueKind.Object } parameters) throw Invalid();
        switch (frame.ActionId)
        {
            case "result.create":
                _store.ValidateCreateAccess(admission.Principal, RequiredString(parameters, "source_id"),
                    frame.IdempotencyKey!, admission.RequestDigest, OptionalInteger(parameters, "ttl_seconds"), admission.ToModel);
                break;
            case "result.read":
                _store.ValidateReadAccess(admission.Principal, RequiredString(parameters, "result_ref"),
                    RequiredString(parameters, "snapshot_id"), RequiredString(parameters, "schema_ref"),
                    OptionalInteger(parameters, "limit") ?? throw Invalid(), OptionalFields(parameters, "fields"),
                    OptionalString(parameters, "cursor"), admission.ToModel, ct);
                break;
            case "result.aggregate":
                var aggregate = parameters.GetProperty("aggregate");
                _store.ValidateAggregateAccess(admission.Principal, RequiredString(parameters, "result_ref"),
                    RequiredString(parameters, "snapshot_id"), RequiredString(parameters, "schema_ref"),
                    OptionalFields(aggregate, "group_by") ?? [], SumField(aggregate), admission.ToModel);
                break;
            case "result.release":
                _store.ValidateReleaseAccess(admission.Principal, RequiredString(parameters, "result_ref"), admission.ToModel);
                break;
            default: throw Invalid();
        }
    }

    /// <inheritdoc />
    public async Task<ActionExecutionResult> ExecuteAsync(ActionFrame frame, ActionContext context,
        CancellationToken ct = default)
    {
        var admission = await AdmitAsync(frame, context, ct).ConfigureAwait(false);
        ActionExecutionResult result;
        try { result = await ExecuteAuthorizedAsync(frame, admission, ct).ConfigureAwait(false); }
        catch (ActionNodeException error)
        {
            EmitAudit(admission, frame.ActionId, error.ErrorCode, null);
            throw;
        }
        catch (OperationCanceledException)
        {
            EmitAudit(admission, frame.ActionId, "cancelled-outcome-not-proven", null);
            throw;
        }
        var payload = result.Result!.Value;
        var descriptor = payload.TryGetProperty("descriptor", out var nested) ? nested : payload;
        var reference = descriptor.GetProperty("result_ref").GetString()!;
        EmitAudit(admission, frame.ActionId, "success",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference))));
        return result;
    }

    private async Task<ActionExecutionResult> ExecuteAuthorizedAsync(ActionFrame frame, ResultActionAdmission admission,
        CancellationToken ct)
    {
        var parameters = frame.Params!.Value;
        var principal = admission.Principal;
        object output;
        switch (frame.ActionId)
        {
            case "result.create":
                var source = _sources[RequiredString(parameters, "source_id")];
                output = await _store.CreateAsync(principal, RequiredString(parameters, "source_id"),
                    frame.IdempotencyKey!, admission.RequestDigest,
                    token => source.Capture(principal, parameters, token),
                    OptionalInteger(parameters, "ttl_seconds"), ct, admission.ToModel).ConfigureAwait(false);
                break;
            case "result.read":
                output = _store.Read(principal, RequiredString(parameters, "result_ref"),
                    RequiredString(parameters, "snapshot_id"), RequiredString(parameters, "schema_ref"),
                    OptionalInteger(parameters, "limit")!.Value, OptionalFields(parameters, "fields"),
                    OptionalString(parameters, "cursor"), admission.ToModel, ct);
                break;
            case "result.aggregate":
                var aggregate = parameters.GetProperty("aggregate");
                var grouping = OptionalFields(aggregate, "group_by") ?? [];
                var sumField = SumField(aggregate);
                var result = _store.Aggregate(principal, RequiredString(parameters, "result_ref"),
                    RequiredString(parameters, "snapshot_id"), RequiredString(parameters, "schema_ref"),
                    grouping, sumField, admission.ToModel, ct);
                var groups = result.Groups.Select(group =>
                {
                    var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    for (var index = 0; index < grouping.Count; index++)
                        values.Add(grouping[index], group.GetProperty("keys")[index]);
                    values.Add("count", group.GetProperty("count"));
                    if (sumField is not null) values.Add("sum", group.GetProperty("sum"));
                    return JsonSerializer.SerializeToElement(values);
                }).ToArray();
                output = new ResultAggregate(result.Descriptor, groups);
                // Group field names can be longer than internal positional keys.
                if (JsonSerializer.SerializeToUtf8Bytes(output).Length > 65536)
                    throw new ActionNodeException(429, NpsStatusCodes.LimitResource,
                        "NWP-RESULT-CAPACITY", "Result capacity unavailable.");
                break;
            default:
                output = _store.Release(principal, RequiredString(parameters, "result_ref"), admission.ToModel);
                break;
        }
        return new ActionExecutionResult
        {
            Result = JsonSerializer.SerializeToElement(output),
            SelectedFeatures = _featureSelection,
        };
    }

    private async ValueTask<ResultActionAdmission> AdmitAsync(ActionFrame frame, ActionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var http = context.HttpContext;
        if (http is null) throw Unsupported(); // No verified native-session binding is implemented here.
        ValidateFeatureAdmission(http);
        var projection = http.Request.Headers["X-NPS-Invocation-Context"];
        if (_projectedHttp && projection.Count != 1)
            throw new ActionNodeException(400, "NPS-CLIENT-BAD-FRAME", "NIP-CONTEXT-INVALID", "Invalid invocation context.");
        if (!_projectedHttp && projection.Count != 0) throw Unsupported();
        if (_projectedHttp && context.HttpRequestBody is { ValueKind: JsonValueKind.Object } body
            && (body.TryGetProperty("invocation_context", out _) || body.TryGetProperty("required_features", out _)))
            throw new ActionNodeException(400, "NPS-CLIENT-BAD-FRAME", "NIP-CONTEXT-INVALID", "Invalid invocation context carrier.");
        if (http.Request.ContentType?.Split(';')[0].Trim() != "application/nwp-frame")
            throw new ActionNodeException(415, "NPS-CLIENT-BAD-FRAME",
                "NWP-HTTP-CONTENT-TYPE-UNSUPPORTED", "Canonical NWP frame content type required.");
        var acceptable = !http.Request.Headers.ContainsKey("Accept")
            || MediaTypeHeaderValue.TryParseStrictList(http.Request.Headers.Accept
                .Select(value => value ?? string.Empty).ToArray(), out var accept)
            && accept.Count > 0 && accept.Any(item => item.Quality is not 0
                && item.MediaType.Value is "application/nwp-capsule" or "application/*" or "*/*");
        if (!acceptable)
            throw new ActionNodeException(406, "NPS-CLIENT-BAD-FRAME",
                "NWP-HTTP-ACCEPT-UNSATISFIABLE", "NWP capsule response must be acceptable.");
        ValidateRequest(frame, context);
        try
        {
            var admitted = await _verify(frame, context, ct).ConfigureAwait(false);
            if (admitted?.Principal is null) throw new ActionNodeException(401,
                NpsStatusCodes.AuthUnauthenticated, "NWP-RESULT-IDENTITY-REQUIRED", "Verified result identity required.");
            try { ValidateResourceAccess(frame, admitted, ct); }
            catch (ActionNodeException error)
            {
                EmitAudit(admitted, frame.ActionId, error.ErrorCode, null);
                throw;
            }
            return admitted;
        }
        catch (ActionNodeException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException) { throw PolicyUnavailable(); }
    }

    private void ValidateFeatureAdmission(HttpContext http)
    {
        var values = http.Request.Headers["X-NPS-Required-Features"];
        try { _ = FeatureWireParser.ParseRequiredHeader(values.Select(value => value ?? "").ToArray()); }
        catch (FeatureFormatException) { throw MalformedFeatures(); }
        var required = values.Count == 0 ? null : values[0];
        try { if (required != _featureSelection || !_routeEnabled()) throw Unsupported(); }
        catch (ActionNodeException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException) { throw PolicyUnavailable(); }
    }

    private void ValidateRequest(ActionFrame frame, ActionContext context)
    {
        if (context.HttpRequestBody is not { ValueKind: JsonValueKind.Object } raw) throw Invalid();
        if (raw.TryGetProperty("frame", out var marker)
            && !(marker.ValueKind == JsonValueKind.String && marker.GetString() == "0x11"
                || marker.ValueKind == JsonValueKind.Number && marker.TryGetUInt32(out var number) && number == 0x11))
            throw new ActionNodeException(400, "NPS-CLIENT-BAD-FRAME", "NWP-HTTP-FRAME-BODY-MALFORMED", "Action frame required.");
        ValidateObject(raw, "frame", "action_id", "params", "idempotency_key", "timeout_ms", "async",
            "callback_url", "priority", "request_id");
        RejectDuplicateKeys(raw);
        if (frame.Async || frame.CallbackUrl is not null || context.TaskId is not null
            || frame.Params is not { ValueKind: JsonValueKind.Object } parameters) throw Invalid();
        switch (frame.ActionId)
        {
            case "result.create":
                ValidateObject(parameters, "source_id", "filter", "fields", "order", "ttl_seconds");
                var sourceId = RequiredString(parameters, "source_id");
                if (!_sources.TryGetValue(sourceId, out var source)
                    || !Guid.TryParseExact(frame.IdempotencyKey, "D", out var key)
                    || key.ToString("D")[14] != '4') throw Invalid();
                _ = OptionalInteger(parameters, "ttl_seconds");
                try { source.Validate(parameters); }
                catch (ActionNodeException) { throw; }
                catch (Exception) { throw Invalid(); }
                break;
            case "result.read":
                ValidateObject(parameters, "result_ref", "snapshot_id", "schema_ref", "limit", "fields", "cursor");
                ValidateBinding(parameters);
                if (OptionalInteger(parameters, "limit") is not (>= 1 and <= 1000)) throw Invalid();
                _ = OptionalFields(parameters, "fields");
                _ = OptionalString(parameters, "cursor");
                break;
            case "result.aggregate":
                ValidateObject(parameters, "result_ref", "snapshot_id", "schema_ref", "aggregate");
                ValidateBinding(parameters);
                if (!parameters.TryGetProperty("aggregate", out var aggregate)
                    || aggregate.ValueKind != JsonValueKind.Object) throw Invalid();
                ValidateObject(aggregate, "group_by", "operations");
                var fields = OptionalFields(aggregate, "group_by") ?? [];
                if (fields.Count > 4 || fields.Any(field => field is "count" or "sum")) throw Invalid();
                _ = SumField(aggregate);
                break;
            case "result.release":
                ValidateObject(parameters, "result_ref");
                _ = RequiredString(parameters, "result_ref");
                break;
            default: throw Invalid();
        }
    }

    private static string? SumField(JsonElement aggregate)
    {
        if (!aggregate.TryGetProperty("operations", out var operations)
            || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > 2) throw Invalid();
        var count = false;
        string? sum = null;
        foreach (var operation in operations.EnumerateArray())
        {
            ValidateObject(operation, "func", "field", "alias");
            var func = RequiredString(operation, "func");
            var alias = RequiredString(operation, "alias");
            if (func == "COUNT" && alias == "count" && !count && !operation.TryGetProperty("field", out _)) count = true;
            else if (func == "SUM" && alias == "sum" && sum is null) sum = RequiredString(operation, "field");
            else throw Invalid();
        }
        if (!count) throw Invalid();
        return sum;
    }

    private static void ValidateBinding(JsonElement parameters)
    {
        _ = RequiredString(parameters, "result_ref");
        _ = RequiredString(parameters, "snapshot_id");
        _ = RequiredString(parameters, "schema_ref");
    }

    private static void ValidateObject(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))) throw Invalid();
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw Invalid();
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        OptionalString(parameters, name) ?? throw Invalid();

    private static string? OptionalString(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString())
            || System.Text.Encoding.UTF8.GetByteCount(value.GetString()!) > 4096) throw Invalid();
        return value.GetString();
    }

    private static int? OptionalInteger(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Invalid();
        return number;
    }

    private static IReadOnlyList<string>? OptionalFields(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256) throw Invalid();
        var fields = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(item.GetString()) && System.Text.Encoding.UTF8.GetByteCount(item.GetString()!) <= 128
                ? item.GetString()! : throw Invalid()).ToArray();
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length) throw Invalid();
        return fields;
    }

    private static ActionSpec Spec(string description) => new()
        { Description = description, Async = false, Idempotent = true, RequiredCapability = "nwp:action" };
    private static ActionNodeException Invalid() => ActionNodeException.ParamsInvalid("Invalid result parameters.");
    private static ActionNodeException Unsupported() => new(409, NpsStatusCodes.ClientConflict,
        "NWP-FEATURE-REQUIRED-UNSUPPORTED", "Required result feature is unavailable on this route.");
    private static ActionNodeException MalformedFeatures() => new(400, "NPS-CLIENT-BAD-REQUEST",
        "NWP-FEATURE-REQUIREMENTS-INVALID", "Invalid feature requirements.");
    private static ActionNodeException PolicyUnavailable() => new(503, NpsStatusCodes.ServerUnavailable,
        "NWP-RESULT-POLICY-UNAVAILABLE", "Result admission policy unavailable.");

    private void EmitAudit(ResultActionAdmission admission, string operation, string outcome, string? resultHash)
    {
        try { _audit?.Invoke(new(admission.Principal.Tenant, admission.Principal.SubjectNid, operation, outcome, resultHash)); }
        catch { throw PolicyUnavailable(); }
    }
}
