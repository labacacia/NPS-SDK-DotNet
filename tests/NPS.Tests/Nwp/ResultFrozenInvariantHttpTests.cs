// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

// Executes the frozen semantic cases at the real HTTP middleware/store boundary.
// Trusted fixture setup changes server state; input booleans never become wire authority.
// Authentication here is synthetic; independent real mTLS/NIP tests qualify that boundary.
public sealed partial class ResultActionHttpTests
{
    public static IEnumerable<object[]> FrozenInvariantCases()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "spec/conformance/alpha20/data_result_vectors.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var cases = document.RootElement.GetProperty("cases");
            Assert.Equal(39, cases.GetArrayLength());
            foreach (var item in cases.EnumerateArray())
            {
                using var decoded = JsonDocument.Parse(Convert.FromHexString(item.GetProperty("input_utf8_hex").GetString()!));
                Assert.True(JsonElement.DeepEquals(item.GetProperty("input"), decoded.RootElement));
                var expected = item.GetProperty("expected");
                yield return [item.GetProperty("id").GetString()!, expected.GetProperty("outcome").GetString()!,
                    expected.GetProperty("expected_handler_calls").GetInt32()];
            }
            yield break;
        }
        throw new FileNotFoundException("Frozen result invariants are required.");
    }

    [Theory]
    [MemberData(nameof(FrozenInvariantCases))]
    public async Task FrozenSemanticInvariantExecutesThroughHttp(string id, string outcome, int expectedHandlers)
    {
        if (id == "DR-F23")
            _fixtureRows = JsonSerializer.SerializeToElement(new[] { new { id = "oversized", reason = new string('x', 65537), amount_minor = 1 } });
        var create = Create();
        var descriptor = await Success(await Send(create));
        var frame = Operation("result.read", descriptor, new { limit = 1 });
        string? identity = "owner-fixture", features = ResultActionProvider.FeatureSelection;
        var owner = new ResultPrincipal("tenant", "owner", "policy-1");
        var reference = descriptor.GetProperty("result_ref").GetString()!;
        var release = new ActionFrame { ActionId = "result.release", Params = JsonSerializer.SerializeToElement(new { result_ref = reference }) };
        var projectionWithoutSelection = false;
        switch (id)
        {
            case "DR-F01": features = null; break;
            case "DR-F02": features = "nps.data_results=2"; break;
            case "DR-F03": _enabled = false; break;
            case "DR-F04": _routeAdmitted = false; break;
            case "DR-F05": projectionWithoutSelection = true; break;
            case "DR-F06": identity = null; break;
            case "DR-F07": _verifierUnavailable = true; break;
            case "DR-F08": break;
            case "DR-F09": identity = "cross-tenant-fixture"; break;
            case "DR-F10": identity = "other-fixture"; break;
            case "DR-F11": _resourcePolicy.SetSourceAuthority(Authority() with { Enabled = false }); break;
            case "DR-F12": _time.Advance(300); break;
            case "DR-F13": _time.Advance(299); break;
            case "DR-F14": await Success(await Send(release)); break;
            case "DR-F15":
            case "DR-F16":
                await DisposeAsync();
                if (id == "DR-F16") _fixtureIssuer = "urn:nps:node:test:unproven-successor";
                await InitializeAsync(); // Actual replacement process-local store has no old locators.
                break;
            case "DR-F17": frame = Replace(frame, "snapshot_id", "sha256:" + new string('b', 64)); break;
            case "DR-F18": frame = Replace(frame, "schema_ref", "sha256:" + new string('b', 64)); break;
            case "DR-F19":
                var page = await Success(await Send(frame));
                frame = Operation("result.read", descriptor, new { limit = 1, fields = new[] { "id" },
                    cursor = page.GetProperty("next_cursor").GetString() });
                break;
            case "DR-F20": frame = Replace(frame, "limit", 0); break;
            case "DR-F21": frame = Replace(frame, "limit", 1001); break;
            case "DR-F22": frame = Replace(frame, "limit", true); break;
            case "DR-F23": break;
            case "DR-F24":
                _resourcePolicy.SetSourceAuthority(Authority() with { Fields = ["id", "amount_minor"] });
                frame = Operation("result.read", descriptor, new { limit = 1, fields = new[] { "reason" } });
                break;
            case "DR-F25": _egress = true; break;
            case "DR-F26":
                _resourcePolicy.SetSourceAuthority(Authority() with { AllowModelEgress = true });
                _egress = true;
                break;
            case "DR-F27":
            case "DR-F28":
            case "DR-F29":
                var scope = _store.GetOwnerGrantScope(owner, reference);
                var grant = _resourcePolicy.IssueGrant(owner, scope, "other", ["result.read"], ["id"],
                    _time.GetUtcNow().ToUnixTimeSeconds() + 60);
                frame = Operation("result.read", descriptor, new { limit = 1, fields = new[] { "id" } });
                identity = "other-fixture";
                if (id == "DR-F28") Assert.True(_resourcePolicy.RevokeGrant(owner, grant.GrantId));
                if (id == "DR-F29")
                {
                    var recipient = new ResultPrincipal("tenant", "other", "policy-1");
                    Assert.Equal("NWP-RESULT-NOT-FOUND", Assert.Throws<ActionNodeException>(() =>
                        _resourcePolicy.IssueGrant(recipient, scope, "third", ["result.read"], ["id"],
                            _time.GetUtcNow().ToUnixTimeSeconds() + 60)).ErrorCode);
                    identity = "third-fixture";
                }
                break;
            case "DR-F30":
                // Conceptual describe is not a fifth wire action: read's descriptor/preview
                // is the metadata surface and must not be disclosed to a copied-handle caller.
                identity = "other-fixture";
                break;
            case "DR-F31": frame = new() { ActionId = "result.execute_sql", Params = JsonSerializer.SerializeToElement(new { }) }; break;
            case "DR-F32":
                for (var count = 1; count < 16; count++) await Success(await Send(Create()));
                frame = Create();
                break;
            case "DR-F33": frame = Create(); break;
            case "DR-F34": frame = Replace(create, "ttl_seconds", 9); break;
            case "DR-F35": frame = Operation("result.aggregate", descriptor,
                new { aggregate = new { group_by = new[] { "reason" }, operations = new[] { new { func = "COUNT", alias = "count" } } } }); break;
            case "DR-F36": frame = release; break;
            case "DR-F37": identity = "other-fixture"; frame = Replace(frame, "snapshot_id", "sha256:" + new string('b', 64)); break;
            case "DR-F38": identity = "other-fixture"; _time.Advance(300); break;
            case "DR-F39": await Success(await Send(release)); frame = release; break;
            default: throw new InvalidOperationException("Unmapped frozen case: " + id);
        }
        var before = _handlerCalls;
        using var request = Request(frame, features, identity);
        if (id == "DR-F06") request.Headers.Add("X-NWP-Agent", "owner");
        if (projectionWithoutSelection) request.Headers.Add("X-NPS-Invocation-Context", "untrusted-fixture");
        using var response = await _client.SendAsync(request);
        switch (outcome)
        {
            case "FEATURE_UNSUPPORTED": await AssertError(response, 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED"); break;
            case "UNAUTHENTICATED": await AssertError(response, 401, "NWP-RESULT-IDENTITY-REQUIRED"); break;
            case "POLICY_UNAVAILABLE": await AssertError(response, 503, "NWP-RESULT-POLICY-UNAVAILABLE"); break;
            case "RESULT_UNAVAILABLE": await AssertError(response, 404, "NWP-RESULT-NOT-FOUND"); break;
            case "SNAPSHOT_MISMATCH": await AssertError(response, 409, "NWP-RESULT-SNAPSHOT-MISMATCH"); break;
            case "INVALID_PARAMS": await AssertError(response, 422, "NWP-ACTION-PARAMS-INVALID"); break;
            case "CAPACITY": await AssertError(response, 429, "NWP-RESULT-CAPACITY"); break;
            case "KEY_CONFLICT": await AssertError(response, 409, "NWP-RESULT-REPLAY-CONFLICT"); break;
            case "PAGE": Assert.Single((await Success(response)).GetProperty("rows").EnumerateArray()); break;
            case "CREATED": Assert.Equal(3, (await Success(response)).GetProperty("row_count").GetInt32()); break;
            case "AGGREGATE":
                Assert.Equal(3, (await Success(response)).GetProperty("groups").EnumerateArray().Sum(group => group.GetProperty("count").GetInt32())); break;
            case "RELEASED": Assert.True((await Success(response)).GetProperty("released").GetBoolean()); break;
            default: throw new InvalidOperationException("Unmapped frozen outcome: " + outcome);
        }
        Assert.Equal(expectedHandlers, _handlerCalls - before);
        if (expectedHandlers == 0)
        {
            var error = await response.Content.ReadAsStringAsync();
            foreach (var secret in new[] { reference, descriptor.GetProperty("snapshot_id").GetString()!, "amount_minor", "row_count" })
                Assert.DoesNotContain(secret, error);
        }
    }

    [Theory]
    [InlineData(null, 409, "NWP-FEATURE-REQUIRED-UNSUPPORTED")]
    [InlineData("nps.data_results=01", 400, "NWP-FEATURE-REQUIREMENTS-INVALID")]
    [InlineData("nps.data_results=1", 422, "NWP-ACTION-PARAMS-INVALID")]
    public async Task UnknownOperationRetainsFeatureAdmissionPrecedence(string? features, int status, string error)
    {
        await AssertError(await Send(new() { ActionId = "result.execute_sql", Params = JsonSerializer.SerializeToElement(new { }) }, features), status, error);
        Assert.Equal(0, _handlerCalls);
        Assert.Equal(0, _verifications);
        Assert.Equal(0, _captures);
    }

    private static ActionFrame Replace(ActionFrame frame, string field, object value)
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(frame.Params!.Value.GetRawText())!;
        parameters[field] = JsonSerializer.SerializeToElement(value);
        return frame with { Params = JsonSerializer.SerializeToElement(parameters) };
    }
}
