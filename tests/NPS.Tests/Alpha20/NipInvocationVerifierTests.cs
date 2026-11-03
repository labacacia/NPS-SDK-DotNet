// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.NIP.Invocation;
using NSec.Cryptography;

namespace NPS.Tests.Alpha20;

/// <summary>Actual Ed25519 admission with a replay spy; durable/transport qualification is tested separately.</summary>
public sealed class NipInvocationVerifierTests : IDisposable
{
    private const string Issuer = "urn:nps:node:fixture:gateway", Subject = "urn:nps:agent:fixture:alice";
    private const string Audience = "service:results";
    private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);
    private readonly Clock _time = new();
    private readonly ReplaySpy _replays = new();
    private bool _enabled = true, _keyPresent = true, _authorized = true, _schemaValid = true;
    private readonly HashSet<string> _channel = [Issuer];
    private static readonly FeatureSelection[] Features = [new("nps.data_results", 1), new("nps.invocation_context", 1)];

    private NipInvocationVerificationOptions Options => new()
    {
        Enabled = () => _enabled, Audience = Audience, ChannelIssuers = () => _channel,
        SelectedFeatures = Features, AllowedCapabilities = new HashSet<string> { "nwp:action" },
        AllowedScopes = new HashSet<string> { "results:read" }, MaximumAssurance = "verified",
        TimeProvider = _time, ReplayStore = _replays,
        ResolveKey = (_, _, _) => ValueTask.FromResult<PublicKey?>(_keyPresent ? _key.PublicKey : null),
        ValidateOperation = (_, _) => ValueTask.FromResult(_schemaValid),
        Authorize = (claim, _, _) => ValueTask.FromResult(_authorized
            && claim.Capabilities.Contains("nwp:action") && claim.Scope.Contains("results:read")),
    };

    private static JsonElement Request() => NipInvocationJson.ResultActionRequest(
        """{"action_id":"result.read","params":{"result_ref":"opaque","snapshot_id":"snapshot","schema_ref":"schema","limit":1}}""",
        Audience, Features);

    private Dictionary<string, object?> Payload(JsonElement request) => new()
    {
        ["profile"] = "nps.invocation_context", ["revision"] = 1, ["issuer"] = Issuer,
        ["key_id"] = "fixture-key", ["audience"] = Audience, ["subject"] = Subject, ["assurance"] = "verified",
        ["capabilities"] = new[] { "nwp:action" }, ["scope"] = new[] { "results:read" },
        ["issued_at"] = 1_800_000_000, ["expires_at"] = 1_800_000_060,
        ["projection_id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), ["request_digest"] = NipInvocationJson.RequestDigest(request),
    };

    private string Carrier(JsonElement request, Action<Dictionary<string, object?>>? mutate = null)
    {
        var payload = Payload(request);
        mutate?.Invoke(payload);
        var value = JsonSerializer.SerializeToElement(payload);
        var bytes = "NPS-CONTEXT-PROJECTION/1\0"u8.ToArray().Concat(NipInvocationJson.Canonicalize(value)).ToArray();
        var signature = SignatureAlgorithm.Ed25519.Sign(_key, bytes);
        return Encode(JsonSerializer.SerializeToUtf8Bytes(new { payload, signature = Encode(signature) }));
    }

    [Theory]
    [MemberData(nameof(NipInvocationJsonTests.SharedVectors), MemberType = typeof(NipInvocationJsonTests))]
    public async Task FixedSharedSignatureAndDigestAreConsumedIndependently(string id, JsonElement vector)
    {
        var corpus = NipInvocationJsonTests.ReadCorpus();
        var spki = Convert.FromHexString(corpus.GetProperty("test_key").GetProperty("public_spki_der_hex").GetString()!);
        Assert.Equal(44, spki.Length);
        Assert.Equal("302a300506032b6570032100", Convert.ToHexStringLower(spki[..12]));
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, spki[^32..], KeyBlobFormat.RawPublicKey);
        var request = vector.GetProperty("request");
        var payload = vector.GetProperty("payload");
        var options = Options with
        {
            Audience = payload.GetProperty("audience").GetString()!,
            SelectedFeatures = [new("nps.invocation_context", 1)],
            AllowedCapabilities = payload.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal),
            AllowedScopes = payload.GetProperty("scope").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal),
            ResolveKey = (_, _, _) => ValueTask.FromResult<PublicKey?>(publicKey),
            Authorize = (_, _, _) => ValueTask.FromResult(true),
        };
        var carriers = new[] { vector.GetProperty("carrier_base64url").GetString()! };
        if (id == "numbers")
        {
            await Error("INVALID", async () => _ = await NipInvocationVerifier.AdmitAsync(carriers, request, new object(), options));
            Assert.Equal(0, _replays.Calls); // Explicit unsupported schema, never a numeric qualification pass.
        }
        else
        {
            var admitted = await NipInvocationVerifier.AdmitAsync(carriers, request, new object(), options);
            Assert.Equal(payload.GetProperty("subject").GetString(), admitted.Claims.Subject);
            Assert.Equal(1, _replays.Calls);
        }
    }

    [Fact]
    public async Task ValidProjectionClaimsReplayOnceAndRevalidatesOnlyTheSameHostInvocation()
    {
        var request = Request();
        var carrier = Carrier(request);
        var invocation = new object();
        var admitted = await NipInvocationVerifier.AdmitAsync([carrier], request, invocation, Options);
        Assert.Equal(Subject, admitted.Claims.Subject);
        Assert.Equal(1, _replays.Calls);
        await NipInvocationVerifier.RevalidateAsync(admitted, [carrier], request, invocation, Options);
        Assert.Equal(1, _replays.Calls);
        await Error("BINDING-MISMATCH", () => NipInvocationVerifier.RevalidateAsync(admitted, [carrier], request, new object(), Options));
        await Error("REPLAY", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), Options));
    }

    [Fact]
    public async Task ConcurrentValidDuplicatesAdmitAtMostOne()
    {
        var request = Request();
        var carrier = Carrier(request);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), Options); return "admitted"; }
            catch (NipInvocationException error) { return error.Code; }
        }));
        Assert.Single(outcomes, outcome => outcome == "admitted");
        Assert.Single(outcomes, outcome => outcome == "NIP-CONTEXT-REPLAY");
    }

    [Theory]
    [InlineData("channel", "UNTRUSTED")]
    [InlineData("key", "UNTRUSTED")]
    [InlineData("disabled", "UNTRUSTED")]
    [InlineData("policy", "BINDING-MISMATCH")]
    [InlineData("schema", "BINDING-MISMATCH")]
    public async Task FailedAdmissionNeverConsumesReplay(string failure, string error)
    {
        var request = Request();
        var carrier = Carrier(request);
        switch (failure)
        {
            case "channel": _channel.Clear(); break;
            case "key": _keyPresent = false; break;
            case "disabled": _enabled = false; break;
            case "policy": _authorized = false; break;
            case "schema": _schemaValid = false; break;
        }
        await Error(error, async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), Options));
        Assert.Equal(0, _replays.Calls);
    }

    [Theory]
    [InlineData("audience", "service:other", "BINDING-MISMATCH")]
    [InlineData("subject", "invalid-nid", "INVALID")]
    [InlineData("assurance", "superuser", "INVALID")]
    [InlineData("request_digest", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "BINDING-MISMATCH")]
    [InlineData("projection_id", "not-random-id", "INVALID")]
    public async Task SignedAssertionsDoNotOverrideReceiverBindings(string field, string value, string error)
    {
        var request = Request();
        var carrier = Carrier(request, payload => payload[field] = value);
        await Error(error, async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), Options));
        Assert.Equal(0, _replays.Calls);
    }

    [Fact]
    public async Task MissingRequiredCapabilityOrScopeDeniesEvenWithValidSignature()
    {
        var request = Request();
        foreach (var field in new[] { "capabilities", "scope" })
            await Error("BINDING-MISMATCH", async () => _ = await NipInvocationVerifier.AdmitAsync(
                [Carrier(request, payload => payload[field] = Array.Empty<string>())], request, new object(), Options));
        Assert.Equal(0, _replays.Calls);
    }

    [Fact]
    public async Task ReceiverCollectionsCannotIntroduceCaseFolding()
    {
        var request = Request();
        var carrier = Carrier(request);
        var wrongScope = Options with { AllowedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RESULTS:READ" } };
        await Error("BINDING-MISMATCH", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), wrongScope));
        var wrongIssuer = Options with { ChannelIssuers = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Issuer.ToUpperInvariant() } };
        await Error("UNTRUSTED", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), wrongIssuer));
        Assert.Equal(0, _replays.Calls);
    }

    [Fact]
    public async Task PolicyDelayExpiryAndKeyWithdrawalAreRecheckedBeforeClaim()
    {
        var request = Request();
        var carrier = Carrier(request);
        var delayed = Options with { Authorize = (_, _, _) => { _time.Seconds += 66; return ValueTask.FromResult(true); } };
        await Error("EXPIRED", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), delayed));
        Assert.Equal(0, _replays.Calls);
        _time.Seconds = 0;
        var revoked = Options with { Authorize = (_, _, _) => { _keyPresent = false; return ValueTask.FromResult(true); } };
        await Error("UNTRUSTED", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], request, new object(), revoked));
        Assert.Equal(0, _replays.Calls);
    }

    [Fact]
    public async Task RetainedAdmissionCannotBypassLiveKeyRevocation()
    {
        var request = Request();
        var carrier = Carrier(request);
        var invocation = new object();
        var admitted = await NipInvocationVerifier.AdmitAsync([carrier], request, invocation, Options);
        _keyPresent = false;
        await Error("UNTRUSTED", () => NipInvocationVerifier.RevalidateAsync(admitted, [carrier], request, invocation, Options));
        Assert.Equal(1, _replays.Calls);
    }

    [Fact]
    public async Task ChangedTargetOrBodyCannotUseTheOriginalSignature()
    {
        var request = Request();
        var carrier = Carrier(request);
        var changed = NipInvocationJson.Parse(request.GetRawText().Replace("opaque", "another"), 65536);
        await Error("BINDING-MISMATCH", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier], changed, new object(), Options));
        var target = NipInvocationJson.Parse(request.GetRawText().Replace(Audience, "service:other"), 65536);
        await Error("BINDING-MISMATCH", async () => _ = await NipInvocationVerifier.AdmitAsync(
            [Carrier(target)], target, new object(), Options));
        Assert.Equal(0, _replays.Calls);
    }

    [Fact]
    public async Task RepeatedNoncanonicalAndMutatedCarriersAreRejected()
    {
        var request = Request();
        var carrier = Carrier(request);
        await Error("INVALID", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier, carrier], request, new object(), Options));
        await Error("INVALID", async () => _ = await NipInvocationVerifier.AdmitAsync([carrier + "="], request, new object(), Options));
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(carrier.Replace('-', '+').Replace('_', '/')
            .PadRight((carrier.Length + 3) / 4 * 4, '=')));
        var tampered = Encode(Encoding.UTF8.GetBytes(raw.Replace(Subject, "urn:nps:agent:fixture:other")));
        await Error("UNTRUSTED", async () => _ = await NipInvocationVerifier.AdmitAsync([tampered], request, new object(), Options));
        Assert.Equal(0, _replays.Calls);
    }

    [Fact]
    public async Task UnavailableReplayNeverAdmitsOrLeaksInfrastructureDetails()
    {
        _replays.Unavailable = true;
        var request = Request();
        var error = await Assert.ThrowsAsync<NipInvocationException>(async () =>
            await NipInvocationVerifier.AdmitAsync([Carrier(request)], request, new object(), Options));
        Assert.Equal("NIP-CONTEXT-VERIFIER-UNAVAILABLE", error.Code);
        Assert.DoesNotContain("private", error.Message);
    }

    private static async Task Error(string suffix, Func<ValueTask> action)
    {
        var error = await Assert.ThrowsAsync<NipInvocationException>(async () => await action());
        Assert.Equal("NIP-CONTEXT-" + suffix, error.Code);
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() => _key.Dispose();

    private sealed class Clock : TimeProvider
    {
        public long Seconds { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + Seconds);
    }
    private sealed class ReplaySpy : INipProjectionReplayStore
    {
        private readonly ConcurrentDictionary<(string, string), byte> _claimed = new();
        public int Calls;
        public bool Unavailable;
        public ValueTask<NipProjectionReplayOutcome> ClaimAsync(string audience, string issuer, string projectionId,
            long acceptBefore, long retainUntil, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Unavailable) throw new IOException("private storage detail");
            return ValueTask.FromResult(_claimed.TryAdd((issuer, projectionId), 0)
                ? NipProjectionReplayOutcome.Claimed : NipProjectionReplayOutcome.Duplicate);
        }
    }
}
