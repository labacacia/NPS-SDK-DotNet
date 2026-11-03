// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.Core;
using NPS.NWP.ActionNode;
using NPS.NWP.Extensions;
using NPS.NWP.Frames;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

/// <summary>
/// Real HTTP middleware/store plumbing with an explicitly synthetic admission fixture.
/// The fixture is NOT NIP authentication, JCS digest verification or bridge qualification.
/// Tests observe capture calls, errors, policy rechecks and exact public capsule contents.
/// </summary>
public sealed partial class ResultActionHttpTests : IAsyncLifetime
{
    private const string Schema = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private IHost _host = null!;
    private HttpClient _client = null!;
    private ResultActionProvider _provider = null!;
    private ResultResourcePolicy _resourcePolicy = null!;
    private ResultResourceStore _store = null!;
    private readonly TestTime _time = new();
    private bool _enabled = true, _routeAdmitted = true, _allow = true, _egress, _verifierUnavailable;
    private string _fixtureIssuer = "urn:nps:node:test:results";
    private JsonElement? _fixtureRows;
    private int _captures, _verifications, _handlerCalls;

    public async Task InitializeAsync()
    {
        _resourcePolicy = new(_fixtureIssuer, timeProvider: _time);
        _resourcePolicy.SetSourceAuthority(Authority());
        var store = _store = new ResultResourceStore(new ResultStoreOptions { IssuerNid = _fixtureIssuer },
            (caller, access) => _allow && _resourcePolicy.Authorize(caller, access), _time);
        var sources = new Dictionary<string, ResultActionSource>
        {
            ["orders"] = new(parameters =>
            {
                // This fixture offers no query options, and explicitly rejects rather than ignores them.
                if (parameters.EnumerateObject().Any(property => property.Name is not ("source_id" or "ttl_seconds")))
                    throw ActionNodeException.ParamsInvalid("Unsupported source query.");
            }, (_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _captures);
                return ValueTask.FromResult(new ResultSnapshot("orders-revision-1", Schema,
                    _fixtureRows ?? JsonSerializer.SerializeToElement(new[] {
                        new { id = "a", reason = "delivery", amount_minor = 1200 },
                        new { id = "b", reason = "quality", amount_minor = 800 },
                        new { id = "c", reason = "delivery", amount_minor = 300 },
                    })));
            }),
        };
        _provider = new ResultActionProvider(store, sources, (frame, context, ct) =>
        {
            Interlocked.Increment(ref _verifications);
            if (_verifierUnavailable) throw new InvalidOperationException("secret fixture verifier detail");
            // Server-owned test allowlist. Not a real authentication scheme; no X-NWP-Agent trust.
            var subject = context.HttpContext!.Request.Headers.Authorization.ToString() switch
            {
                "Bearer owner-fixture" => "owner",
                "Bearer other-fixture" => "other",
                "Bearer third-fixture" => "third",
                "Bearer cross-tenant-fixture" => "owner",
                _ => null,
            };
            if (subject is null) return ValueTask.FromResult<ResultActionAdmission?>(null);
            // Deliberately a fixture digest, NOT evidence for NWP §9.7 canonical request binding.
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(frame.Params?.GetRawText() ?? "null")));
            var tenant = context.HttpContext.Request.Headers.Authorization.ToString() == "Bearer cross-tenant-fixture"
                ? "another-tenant" : "tenant";
            return ValueTask.FromResult<ResultActionAdmission?>(new(new(tenant, subject, "policy-1"), digest, _egress));
        }, () => _enabled && _routeAdmitted);
        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(new CountedProvider(_provider, () => Interlocked.Increment(ref _handlerCalls)));
                services.AddSingleton<IActionTaskStore, InMemoryActionTaskStore>();
                services.AddSingleton<IActionTaskCancellationRegistry, InMemoryActionTaskCancellationRegistry>();
                services.AddSingleton<IIdempotencyCache, InMemoryIdempotencyCache>();
            });
            web.Configure(app => app.UseActionNode<CountedProvider>(options =>
            {
                options.NodeId = _fixtureIssuer;
                options.PathPrefix = "/results";
                _provider.ConfigureNode(options);
            }));
        }).StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [InlineData(null, 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED")]
    [InlineData("nps.data_results=2", 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED")]
    [InlineData("nps.data_results=01", 400, "NWP-FEATURE-REQUIREMENTS-INVALID")]
    [InlineData("nps.data_results=65536", 400, "NWP-FEATURE-REQUIREMENTS-INVALID")]
    [InlineData("nps.data_results=1 ", 400, "NWP-FEATURE-REQUIREMENTS-INVALID")]
    [InlineData("nps.data_results=1,nps.data_results=1", 400, "NWP-FEATURE-REQUIREMENTS-INVALID")]
    [InlineData("nps.other=1,nps.data_results=1", 400, "NWP-FEATURE-REQUIREMENTS-INVALID")]
    [InlineData("nps.data_results=1,nps.invocation_context=1", 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED")]
    [InlineData("nps.other-profile=1", 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED")]
    public async Task InvalidOrUnsupportedAdmissionHasZeroCaptureAndVerifierCalls(string? features, int status, string error)
    {
        await AssertError(await Send(Create(), features: features), status, error);
        Assert.Equal(0, _captures);
        Assert.Equal(0, _verifications);
        Assert.Equal(0, _handlerCalls);
    }

    [Fact]
    public async Task WithdrawnRouteRepeatedHeadersAndProjectionRejectBeforeCapture()
    {
        _enabled = false;
        await AssertError(await Send(Create()), 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED");
        _enabled = true;
        using var request = Request(Create());
        request.Headers.TryAddWithoutValidation("X-NPS-Required-Features", "nps.data_results=1");
        await AssertError(await _client.SendAsync(request), 400, "NWP-FEATURE-REQUIREMENTS-INVALID");
        using var projected = Request(Create());
        projected.Headers.Add("X-NPS-Invocation-Context", "forged");
        await AssertError(await _client.SendAsync(projected), 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED");
        Assert.Equal(0, _captures);
        Assert.Equal(0, _verifications);
        Assert.Equal(0, _handlerCalls);
    }

    [Fact]
    public async Task RawAgentHeaderNeverAuthenticatesAndVerifierFailureIsSanitized()
    {
        using var request = Request(Create(), identity: null);
        request.Headers.Add("X-NWP-Agent", "owner");
        await AssertError(await _client.SendAsync(request), 401, "NWP-RESULT-IDENTITY-REQUIRED");
        _verifierUnavailable = true;
        var response = await Send(Create());
        await AssertError(response, 503, "NWP-RESULT-POLICY-UNAVAILABLE");
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _captures);
        Assert.Equal(0, _handlerCalls);
    }

    [Fact]
    public async Task LifecycleUsesCapsulesExactAggregatesSnapshotPagesAndOwnerRelease()
    {
        var descriptor = await Success(await Send(Create()));
        Assert.Equal(3, descriptor.GetProperty("row_count").GetInt32());
        Assert.False(descriptor.TryGetProperty("rows", out _));
        var first = await Success(await Send(Operation("result.read", descriptor, new { limit = 2 })));
        Assert.Equal("a", first.GetProperty("rows")[0].GetProperty("id").GetString());
        Assert.Equal("b", first.GetProperty("rows")[1].GetProperty("id").GetString());
        var next = first.GetProperty("next_cursor").GetString();
        var last = await Success(await Send(Operation("result.read", descriptor, new { limit = 2, cursor = next })));
        Assert.Single(last.GetProperty("rows").EnumerateArray());
        Assert.Equal("c", last.GetProperty("rows")[0].GetProperty("id").GetString());
        var aggregate = await Success(await Send(Operation("result.aggregate", descriptor, new
        {
            aggregate = new { group_by = new[] { "reason" }, operations = new[] {
                JsonSerializer.SerializeToElement(new { func = "COUNT", alias = "count" }),
                JsonSerializer.SerializeToElement(new { func = "SUM", alias = "sum", field = "amount_minor" }),
            } },
        })));
        var delivery = aggregate.GetProperty("groups").EnumerateArray()
            .Single(group => group.GetProperty("reason").GetString() == "delivery");
        Assert.Equal(2, delivery.GetProperty("count").GetInt64());
        Assert.Equal(1500, delivery.GetProperty("sum").GetInt64());
        var release = new ActionFrame { ActionId = "result.release", Params = JsonSerializer.SerializeToElement(new
            { result_ref = descriptor.GetProperty("result_ref").GetString() }) };
        await AssertError(await Send(release, identity: "other-fixture"), 404, "NWP-RESULT-NOT-FOUND");
        Assert.True((await Success(await Send(release))).GetProperty("released").GetBoolean());
        Assert.True((await Success(await Send(release))).GetProperty("released").GetBoolean());
        await AssertError(await Send(Operation("result.read", descriptor, new { limit = 2 })), 404, "NWP-RESULT-NOT-FOUND");
    }

    [Fact]
    public async Task SameKeyNeverBypassesStorePolicyExpiryOrConflicts()
    {
        var create = Create();
        var descriptor = await Success(await Send(create));
        Assert.Equal(descriptor.GetRawText(), (await Success(await Send(create))).GetRawText());
        Assert.Equal(1, _captures);
        var read = Operation("result.read", descriptor, new { limit = 1 }) with { IdempotencyKey = Guid.NewGuid().ToString() };
        await Success(await Send(read));
        _allow = false;
        await AssertError(await Send(create), 404, "NWP-RESULT-NOT-FOUND");
        await AssertError(await Send(read), 404, "NWP-RESULT-NOT-FOUND");
        _allow = true;
        await AssertError(await Send(create with { Params = JsonSerializer.SerializeToElement(new { source_id = "orders", ttl_seconds = 9 }) }),
            409, "NWP-RESULT-REPLAY-CONFLICT");
        _time.Advance(300);
        await AssertError(await Send(read), 404, "NWP-RESULT-NOT-FOUND");
        var recreated = await Success(await Send(create));
        Assert.NotEqual(descriptor.GetProperty("result_ref").GetString(), recreated.GetProperty("result_ref").GetString());
        Assert.Equal(2, _captures);
    }

    [Fact]
    public async Task EgressDeniesMetadataBeforeCaptureAndRetainedRead()
    {
        _egress = true;
        await AssertError(await Send(Create()), 404, "NWP-RESULT-NOT-FOUND");
        Assert.Equal(0, _captures);
        Assert.Equal(0, _handlerCalls); // Resource policy rejects before provider execution.
        _egress = false;
        var descriptor = await Success(await Send(Create()));
        _egress = true;
        await AssertError(await Send(Operation("result.read", descriptor, new { limit = 1 })), 404, "NWP-RESULT-NOT-FOUND");
        Assert.Equal(1, _handlerCalls); // Only the authorized create entered the handler.
    }

    [Fact]
    public async Task RecipientGrantIsResultFieldAndOperationBoundOnTheHttpRoute()
    {
        var descriptor = await Success(await Send(Create()));
        var owner = new ResultPrincipal("tenant", "owner", "policy-1");
        var grant = _resourcePolicy.IssueGrant(owner,
            _store.GetOwnerGrantScope(owner, descriptor.GetProperty("result_ref").GetString()!),
            "other", ["result.read"], ["id"], _time.GetUtcNow().ToUnixTimeSeconds() + 60);
        var narrowRead = Operation("result.read", descriptor, new { limit = 1, fields = new[] { "id" } });
        var page = await Success(await Send(narrowRead, identity: "other-fixture"));
        Assert.Equal("a", page.GetProperty("rows")[0].GetProperty("id").GetString());
        Assert.False(page.GetProperty("rows")[0].TryGetProperty("amount_minor", out _));
        await AssertError(await Send(Operation("result.read", descriptor, new { limit = 1 }), identity: "other-fixture"),
            404, "NWP-RESULT-NOT-FOUND");
        var second = await Success(await Send(Create()));
        await AssertError(await Send(Operation("result.read", second, new { limit = 1, fields = new[] { "id" } }),
            identity: "other-fixture"), 404, "NWP-RESULT-NOT-FOUND");
        Assert.False(_resourcePolicy.RevokeGrant(new("tenant", "other", "policy-1"), grant.GrantId));
        Assert.True(_resourcePolicy.RevokeGrant(owner, grant.GrantId));
        await AssertError(await Send(narrowRead, identity: "other-fixture"), 404, "NWP-RESULT-NOT-FOUND");
    }

    [Fact]
    public async Task LiveSourceRevocationBlocksRetainedOwnerAndRecipientHttpReads()
    {
        var descriptor = await Success(await Send(Create()));
        var owner = new ResultPrincipal("tenant", "owner", "policy-1");
        _resourcePolicy.IssueGrant(owner, _store.GetOwnerGrantScope(owner, descriptor.GetProperty("result_ref").GetString()!),
            "other", ["result.read"], ["id"], _time.GetUtcNow().ToUnixTimeSeconds() + 60);
        _resourcePolicy.SetSourceAuthority(Authority() with { Enabled = false });
        foreach (var identity in new[] { "owner-fixture", "other-fixture" })
            await AssertError(await Send(Operation("result.read", descriptor,
                new { limit = 1, fields = new[] { "id" } }), identity: identity), 404, "NWP-RESULT-NOT-FOUND");
    }

    [Theory]
    [InlineData("{\"source_id\":\"orders\",\"url\":\"https://attacker.test\"}")]
    [InlineData("{\"source_id\":\"orders\",\"source_id\":\"orders\"}")]
    [InlineData("{\"source_id\":\"orders\",\"filter\":{\"id\":{\"$eq\":\"a\"}}}")]
    [InlineData("{\"source_id\":\"unknown\"}")]
    public async Task UnsupportedMalformedQueriesNeverReachSource(string parameters)
    {
        await AssertError(await Send(Create() with { Params = JsonDocument.Parse(parameters).RootElement.Clone() }),
            422, "NWP-ACTION-PARAMS-INVALID");
        Assert.Equal(0, _captures);
        Assert.Equal(0, _handlerCalls);
    }

    [Fact]
    public async Task BoundsAndMediaTypesFailBeforeCaptureAndManifestDoesNotAdvertise()
    {
        using var request = Request(Create());
        request.Content = new StringContent(new string(' ', 65537), Encoding.UTF8, "application/nwp-frame");
        await AssertError(await _client.SendAsync(request), 413, "NWP-HTTP-BODY-TOO-LARGE");
        using var wrongMedia = Request(Create());
        wrongMedia.Content!.Headers.ContentType = new("application/json");
        await AssertError(await _client.SendAsync(wrongMedia), 415, "NWP-HTTP-CONTENT-TYPE-UNSUPPORTED");
        using var wrongAccept = Request(Create());
        wrongAccept.Headers.Accept.Add(new("text/plain"));
        await AssertError(await _client.SendAsync(wrongAccept), 406, "NWP-HTTP-ACCEPT-UNSATISFIABLE");
        using var malformedAccept = Request(Create());
        malformedAccept.Headers.TryAddWithoutValidation("Accept", "invalid media type");
        await AssertError(await _client.SendAsync(malformedAccept), 406, "NWP-HTTP-ACCEPT-UNSATISFIABLE");
        Assert.DoesNotContain("feature_negotiation", await _client.GetStringAsync("/results/.nwm"));
        Assert.Equal(0, _captures);
        Assert.Equal(0, _handlerCalls);
    }

    private Task<HttpResponseMessage> Send(ActionFrame frame, string? features = ResultActionProvider.FeatureSelection,
        string? identity = "owner-fixture") => _client.SendAsync(Request(frame, features, identity));

    private static HttpRequestMessage Request(ActionFrame frame, string? features = ResultActionProvider.FeatureSelection,
        string? identity = "owner-fixture")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/results/invoke")
        {
            Content = new StringContent(JsonSerializer.Serialize(frame, Json), Encoding.UTF8, "application/nwp-frame"),
        };
        if (features is not null) request.Headers.TryAddWithoutValidation("X-NPS-Required-Features", features);
        if (identity is not null) request.Headers.Authorization = new("Bearer", identity);
        return request;
    }

    private static ActionFrame Create() => new() { ActionId = "result.create", IdempotencyKey = Guid.NewGuid().ToString(),
        Params = JsonSerializer.SerializeToElement(new { source_id = "orders" }) };

    private static ResultSourceAuthority Authority() => new("tenant", "owner", "orders", "policy-1",
        ["id", "reason", "amount_minor"], ["result.create", "result.read", "result.aggregate", "result.release"],
        AllowDelegation: true);

    private static ActionFrame Operation(string action, JsonElement descriptor, object extra)
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(extra))!;
        foreach (var name in new[] { "result_ref", "snapshot_id", "schema_ref" }) parameters.Add(name, descriptor.GetProperty(name));
        return new() { ActionId = action, Params = JsonSerializer.SerializeToElement(parameters) };
    }

    private static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/nwp-capsule", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ResultActionProvider.FeatureSelection, response.Headers.GetValues("X-NPS-Selected-Features").Single());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data")[0].Clone();
    }

    private static async Task AssertError(HttpResponseMessage response, int status, string error)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/nwp-error+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("X-NPS-Selected-Features"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, document.RootElement.GetProperty("error").GetString());
    }

    private sealed class TestTime : TimeProvider
    {
        private long _seconds;
        public void Advance(long seconds) => _seconds += seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + _seconds);
        public override long GetTimestamp() => _seconds;
        public override long TimestampFrequency => 1;
    }

    private sealed class CountedProvider(ResultActionProvider inner, Action entered) : IActionNodeProvider
    {
        public bool ManagesIdempotency => inner.ManagesIdempotency;
        public int? MaxRequestBytes => inner.MaxRequestBytes;
        public string ErrorMediaType => inner.ErrorMediaType;
        public ActionNodeException? GetUnknownActionError(ActionFrame frame, Microsoft.AspNetCore.Http.HttpContext context) => inner.GetUnknownActionError(frame, context);
        public Task AuthorizeAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default) =>
            inner.AuthorizeAsync(frame, context, ct);
        public Task<ActionExecutionResult> ExecuteAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default)
        {
            entered();
            return inner.ExecuteAsync(frame, context, ct);
        }
    }

}
