// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using NPS.Core.Alpha20;
using NPS.NWP.Frames;

namespace NPS.NWP.Results;

/// <summary>
/// Structured result HTTP failure without raw server bodies/credentials. OutcomeUnknown means
/// this attempt may already have allocated a result; retry only through the same logical key
/// and appropriate fresh identity proof. False makes no claim about a previous attempt.
/// </summary>
public sealed class ResultHttpException(string message, bool outcomeUnknown, int? httpStatus = null,
    string? npsStatus = null, string? errorCode = null) : Exception(message)
{
    /// <summary>Whether the current attempt's allocation/execution outcome remains uncertain.</summary>
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    /// <summary>Observed HTTP response status, absent on local/transport failures.</summary>
    public int? HttpStatus { get; } = httpStatus;
    /// <summary>Bounded structured NPS status, absent when the error envelope is invalid.</summary>
    public string? NpsStatus { get; } = npsStatus;
    /// <summary>Bounded structured protocol error code, absent on local/invalid-envelope failures.</summary>
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>
/// Host-owned request signer for a projected route. Inputs are the exact body/pinned target/
/// requirements sent by this client. Return a canonical NIP carrier derived from independently
/// admitted caller authority, never from a model's claimed tenant/subject. No credential is logged.
/// </summary>
public delegate ValueTask<string> ResultHttpRequestSigner(string frameJson, string target,
    IReadOnlyList<FeatureSelection> requiredFeatures, CancellationToken ct);

/// <summary>
/// Experimental bounded result ActionFrame client. Fetches affirmative version-1 NWM support
/// on the pinned HTTPS route before EVERY operation, disables redirects, checks exact success
/// echo and never retries or falls back to a legacy/unverified endpoint. Owns its transport.
/// Cancellation after dispatch does not prove that allocation was rolled back.
/// </summary>
public sealed class ResultHttpClient : IDisposable
{
    private const int ResponseLimit = 131072;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
    private readonly HttpClient _http;
    private readonly Uri _manifest, _invoke;
    private readonly string _issuer, _required;
    private readonly IReadOnlyList<FeatureSelection> _features;
    private readonly ResultHttpRequestSigner? _sign;
    private readonly object _manifestGate = new();
    private uint _manifestVersion;
    private string? _manifestHash;

    /// <summary>
    /// Pin a node's HTTPS base path and expected issuer NID. Configure standard TLS/mTLS trust
    /// on the privately owned handler. Redirects are disabled AFTER configuration. Supplying
    /// a signer explicitly selects the projected revision-1 leg; absence selects direct HTTP.
    /// No network operation or default feature enablement occurs in this constructor.
    /// </summary>
    public ResultHttpClient(Uri baseUri, string expectedIssuerNid, ResultHttpRequestSigner? signer = null,
        Action<SocketsHttpHandler>? configureTransport = null)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps || baseUri.UserInfo.Length != 0
            || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 || !Text(expectedIssuerNid, 512))
            throw new ArgumentException("A pinned HTTPS node route and issuer are required.");
        var route = baseUri.AbsoluteUri.TrimEnd('/');
        _manifest = new(route + "/.nwm");
        _invoke = new(route + "/invoke");
        _issuer = expectedIssuerNid;
        _sign = signer;
        _required = signer is null ? "nps.data_results=1" : "nps.data_results=1,nps.invocation_context=1";
        _features = Array.AsReadOnly(FeatureWireParser.ParseRequiredHeader([_required]).ToArray());
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        configureTransport?.Invoke(handler);
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        _http = new(handler) { Timeout = TimeSpan.FromSeconds(120), DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }

    /// <summary>
    /// Create one immutable source result without preview rows. Query may contain only the
    /// advertised source's filter/fields/order subset; no URL/SQL/code parameter is accepted.
    /// The same UUID-v4 key and same parameters recover a retained authorized descriptor.
    /// Cancellation propagates as OperationCanceledException. If a POST may have started,
    /// allocation can already exist; recover explicitly with that same key/parameters,
    /// fresh identity proof and current authorization within the result's process TTL.
    /// </summary>
    public async Task<ResultDescriptor> CreateAsync(string sourceId, Guid idempotencyKey,
        JsonElement? query = null, int? ttlSeconds = null, CancellationToken ct = default)
    {
        if (!Text(sourceId, 128) || idempotencyKey.ToString("D")[14] != '4'
            || ttlSeconds is < 1 or > 900) throw new ArgumentException("Invalid result create parameters.");
        var parameters = new Dictionary<string, object?> { ["source_id"] = sourceId };
        if (ttlSeconds is not null) parameters.Add("ttl_seconds", ttlSeconds);
        if (query is { } value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("A source query object is required.");
            foreach (var field in value.EnumerateObject())
            {
                if (field.Name is not ("filter" or "fields" or "order") || parameters.ContainsKey(field.Name))
                    throw new ArgumentException("Unsupported create query parameter.");
                parameters.Add(field.Name, field.Value);
            }
        }
        var payload = await InvokeAsync("result.create", parameters, idempotencyKey.ToString("D"), ct).ConfigureAwait(false);
        var result = Decode<ResultDescriptor>(payload);
        ValidateDescriptor(result);
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 8) throw Protocol();
        return result;
    }

    /// <summary>Read one bounded page from the descriptor's exact issuer/snapshot/schema; no automatic pagination.</summary>
    public async Task<ResultPage> ReadAsync(ResultDescriptor descriptor, int limit,
        IReadOnlyList<string>? fields = null, string? cursor = null, CancellationToken ct = default)
    {
        ValidateDescriptor(descriptor, received: false);
        if (limit is < 1 or > 1000 || cursor is not null && !Text(cursor, 4096))
            throw new ArgumentException("Invalid page bounds.");
        if (fields is not null) ValidateFields(fields);
        var payload = await InvokeAsync("result.read", new
        {
            result_ref = descriptor.ResultRef, snapshot_id = descriptor.SnapshotId, schema_ref = descriptor.SchemaRef,
            limit, fields, cursor,
        }, null, ct).ConfigureAwait(false);
        var page = Decode<ResultPage>(payload);
        ValidateDescriptor(page.Descriptor);
        if (page.Descriptor != descriptor || page.Rows.Count > limit || page.Rows.Count > 1000
            || page.Rows.Any(row => row.ValueKind != JsonValueKind.Object)
            || Encoding.UTF8.GetByteCount(payload.GetRawText()) > 65536
            || page.NextCursor is not null && !Text(page.NextCursor, 4096)) throw Protocol();
        return page;
    }

    /// <summary>Compute the reference exact COUNT and optional Int64 SUM subset over the captured result.</summary>
    public async Task<ResultAggregate> AggregateAsync(ResultDescriptor descriptor, IReadOnlyList<string> groupBy,
        string? sumField = null, CancellationToken ct = default)
    {
        ValidateDescriptor(descriptor, received: false);
        if (groupBy.Count > 4 || groupBy.Any(field => field is "count" or "sum")) throw new ArgumentException("Invalid grouping.");
        if (groupBy.Count != 0) ValidateFields(groupBy);
        if (sumField is not null && !Text(sumField, 128)) throw new ArgumentException("Invalid sum field.");
        var operations = new List<JsonElement> { JsonSerializer.SerializeToElement(new { func = "COUNT", alias = "count" }) };
        if (sumField is not null) operations.Add(JsonSerializer.SerializeToElement(new { func = "SUM", field = sumField, alias = "sum" }));
        var payload = await InvokeAsync("result.aggregate", new
        {
            result_ref = descriptor.ResultRef, snapshot_id = descriptor.SnapshotId, schema_ref = descriptor.SchemaRef,
            aggregate = new { group_by = groupBy, operations },
        }, null, ct).ConfigureAwait(false);
        var result = Decode<ResultAggregate>(payload);
        ValidateDescriptor(result.Descriptor);
        if (result.Descriptor != descriptor || result.Groups.Count > 1000
            || result.Groups.Any(group => group.ValueKind != JsonValueKind.Object)
            || Encoding.UTF8.GetByteCount(payload.GetRawText()) > 65536) throw Protocol();
        return result;
    }

    /// <summary>Owner-authorized idempotent release on this pinned issuer; a reference is not authority.</summary>
    public async Task<ResultRelease> ReleaseAsync(ResultDescriptor descriptor, CancellationToken ct = default)
    {
        ValidateDescriptor(descriptor, received: false);
        var result = Decode<ResultRelease>(await InvokeAsync("result.release", new { result_ref = descriptor.ResultRef }, null, ct).ConfigureAwait(false));
        if (result.ResultRef != descriptor.ResultRef || !result.Released) throw Protocol();
        return result;
    }

    private async Task<JsonElement> InvokeAsync(string operation, object parameters, string? key, CancellationToken ct)
    {
        var frame = new ActionFrame { ActionId = operation, Params = JsonSerializer.SerializeToElement(parameters, Json),
            IdempotencyKey = key, RequestId = Guid.NewGuid().ToString("D") };
        var raw = JsonSerializer.Serialize(frame, Json);
        return await SendFrameAsync(raw, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forward one exact original result ActionFrame without DTO defaults or semantic rewriting.
    /// Intended for an authenticated gateway that separately admits the public leg. The usual
    /// pinned discovery, fresh signer, bounded capsule/error and no-retry rules still apply.
    /// Returns one JSON payload; gateways must preserve its provenance and exposure policy.
    /// This lower-level API does not apply typed descriptor/page consistency checks.
    /// Cancellation after possible dispatch does not prove rollback or non-execution;
    /// it propagates without an automatic retry.
    /// </summary>
    public async Task<JsonElement> SendFrameAsync(string raw, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(raw) > 65536) throw new ArgumentException("Result request exceeds its bound.");
        string operation;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
            CheckKeys(document.RootElement);
            operation = document.RootElement.GetProperty("action_id").GetString()!;
            if (operation is not ("result.create" or "result.read" or "result.aggregate" or "result.release")
                || document.RootElement.GetProperty("params").ValueKind != JsonValueKind.Object)
                throw new ArgumentException("A result ActionFrame is required.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ArgumentException("Invalid result ActionFrame."); }
        await DiscoverAsync(operation, ct).ConfigureAwait(false); // No signing/POST without affirmative route support.
        using var request = new HttpRequestMessage(HttpMethod.Post, _invoke)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/nwp-frame"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/nwp-capsule"));
        request.Headers.Add("X-NPS-Required-Features", _required);
        if (_sign is not null)
        {
            var carrier = await _sign(raw, _issuer, _features, ct).ConfigureAwait(false);
            if (!Text(carrier, 21846) || carrier.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
                throw new ResultHttpException("Invalid locally produced invocation context.", false);
            request.Headers.Add("X-NPS-Invocation-Context", carrier);
        }
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new ResultHttpException("Result transport failed; outcome may be unknown.", true); }
        using (response)
        {
            if (response.RequestMessage?.RequestUri != _invoke) throw Protocol();
            if (!response.IsSuccessStatusCode)
            {
                if (response.Headers.Contains("X-NPS-Selected-Features") || response.Content.Headers.ContentType?.MediaType != "application/nwp-error+json")
                    throw Protocol((int)response.StatusCode);
                var error = await BodyAsync(response, ResponseLimit, true, ct).ConfigureAwait(false);
                var status = RequiredText(error, "status", 128);
                var code = RequiredText(error, "error", 128);
                throw new ResultHttpException("NWP result request rejected.", (int)response.StatusCode >= 500,
                    (int)response.StatusCode, status, code);
            }
            if (response.Content.Headers.ContentType?.MediaType != "application/nwp-capsule"
                || !response.Headers.TryGetValues("X-NPS-Selected-Features", out var values)
                || !values.SequenceEqual(new[] { _required })) throw Protocol((int)response.StatusCode);
            var capsule = await BodyAsync(response, ResponseLimit, true, ct).ConfigureAwait(false);
            if (capsule.ValueKind != JsonValueKind.Object || !capsule.TryGetProperty("count", out var count)
                || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number != 1
                || !capsule.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1)
                throw Protocol((int)response.StatusCode);
            return data[0].Clone();
        }
    }

    private async Task DiscoverAsync(string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _manifest);
        request.Headers.Accept.Add(new("application/nwp-manifest+json"));
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { throw Discovery(); }
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != _manifest
                || response.Content.Headers.ContentType?.MediaType != "application/nwp-manifest+json") throw Discovery();
            var manifest = await BodyAsync(response, 65536, false, ct).ConfigureAwait(false);
            try
            {
                if (RequiredText(manifest, "node_id", 512) != _issuer || manifest.GetProperty("node_type").GetString() is not ("action" or "complex" or "bridge")
                    || !manifest.GetProperty("manifest_version").TryGetUInt32(out var version) || version == 0
                    || !response.Headers.TryGetValues("X-NWM-Version", out var header)
                    || !header.SequenceEqual(new[] { version.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                    || !manifest.GetProperty("auth").GetProperty("required").GetBoolean()
                    || manifest.GetProperty("auth").GetProperty("identity_type").GetString() is not ("nip-cert" or "bearer")
                    || !manifest.GetProperty("auth").GetProperty("required_capabilities").EnumerateArray().Any(value => value.GetString() == "nwp:action")
                    || manifest.TryGetProperty("manifest_updated_at", out _) && !DateTimeOffset.TryParse(RequiredText(manifest, "manifest_updated_at", 64),
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)
                    || !manifest.GetProperty("actions").TryGetProperty(operation, out var action)
                    || action.GetProperty("required_capability").GetString() != "nwp:action") throw Discovery();
                var invoke = new Uri(_manifest, RequiredText(manifest.GetProperty("endpoints"), "invoke", 2048));
                if (invoke != _invoke) throw Discovery();
                var profile = manifest.GetProperty("profiles").GetProperty("feature_negotiation");
                if (Encoding.UTF8.GetByteCount(profile.GetRawText()) > 16384
                    || profile.EnumerateObject().Count() != 2 || profile.GetProperty("version").GetRawText() != "1") throw Discovery();
                var block = "{\"feature_negotiation\":1,\"feature_offer\":" + profile.GetProperty("feature_offer").GetRawText() + "}";
                var offer = FeatureWireParser.ParseOffer(block);
                if (_features.Any(pair => !offer.Features.TryGetValue(pair.Id, out var revisions)
                    || !revisions.Any(revision => revision.Revision == pair.Revision && revision.Lifecycle is "experimental" or "stable"))) throw Discovery();
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.GetRawText())));
                lock (_manifestGate)
                {
                    if (version < _manifestVersion || version == _manifestVersion && hash != _manifestHash) throw Discovery();
                    _manifestVersion = version;
                    _manifestHash = hash;
                }
            }
            catch (Exception error) when (error is ResultHttpException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
            { throw Discovery(); }
        }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, int maximum, bool uncertain, CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentLength is { } length && length > maximum) throw new ResultHttpException("Response exceeds its bound.", uncertain);
            using var buffer = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > maximum) throw new ResultHttpException("Response exceeds its bound.", uncertain);
                buffer.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            CheckKeys(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (ResultHttpException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new ResultHttpException("Invalid or incomplete response.", uncertain); }
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                CheckKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }

    private void ValidateDescriptor(ResultDescriptor descriptor, bool received = true)
    {
        if (descriptor is null || descriptor.IssuerNid != _issuer || !Text(descriptor.ResultRef, 512) || !Text(descriptor.SnapshotId, 512)
            || !Text(descriptor.SchemaRef, 71) || descriptor.SchemaRef.Length != 71
            || !descriptor.SchemaRef.StartsWith("sha256:", StringComparison.Ordinal)
            || descriptor.SchemaRef.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0 || descriptor.RowCount is < 0 or > 100000
            || descriptor.Retention != "process" || descriptor.ExpiresAt <= descriptor.CreatedAt
            || (decimal)descriptor.ExpiresAt - descriptor.CreatedAt > 900)
        {
            if (received) throw Protocol();
            throw new ResultHttpException("Invalid result descriptor for this issuer; no operation was sent.", false);
        }
    }
    private static void ValidateFields(IReadOnlyList<string> fields)
    {
        if (fields.Count is < 1 or > 256 || fields.Any(field => !Text(field, 128))
            || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count) throw new ArgumentException("Invalid fields.");
    }
    private static T Decode<T>(JsonElement value)
    {
        try { return value.Deserialize<T>(Json) ?? throw Protocol(); }
        catch (ResultHttpException) { throw; }
        catch { throw Protocol(); }
    }
    private static string RequiredText(JsonElement value, string field, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String || !Text(property.GetString()!, maximum)) throw Protocol();
        return property.GetString()!;
    }
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= maximum;
    private static ResultHttpException Discovery() => new("Affirmative authenticated result-route support is unavailable; no operation was sent.", false);
    private static ResultHttpException Protocol(int? status = null) => new("Invalid result protocol response; outcome may be unknown.", true, status);
    /// <summary>Release owned connections; this does not release server-side result resources.</summary>
    public void Dispose() => _http.Dispose();
}
