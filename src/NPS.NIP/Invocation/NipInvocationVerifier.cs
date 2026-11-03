// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NPS.Core.Alpha20;
using NSec.Cryptography;

namespace NPS.NIP.Invocation;

/// <summary>Atomic durable replay outcome; expiry does not imply that an operation executed.</summary>
public enum NipProjectionReplayOutcome { Claimed, Duplicate, Expired }

/// <summary>
/// Deployment replay boundary. Claim must atomically check its current clock, insert
/// (issuer, projection ID) if absent and retain until at least expires_at+5. The same
/// audience's replicas must share this durable store; failures must throw, not admit.
/// </summary>
public interface INipProjectionReplayStore
{
    /// <summary>Claim a validated envelope; unavailable/capacity storage must fail closed.</summary>
    ValueTask<NipProjectionReplayOutcome> ClaimAsync(string audience, string issuer, string projectionId,
        long acceptBefore, long retainUntil, CancellationToken ct = default);
}

/// <summary>Validated signed gateway assertions, still constrained by receiver-owned policy.</summary>
public sealed record NipInvocationClaims(string Issuer, string KeyId, string Audience, string Subject,
    string Assurance, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Scope,
    long IssuedAt, long ExpiresAt, string ProjectionId, string RequestDigest);

/// <summary>
/// Trusted receiver configuration for one admission. Never construct its channel issuer
/// set, target, ceilings or callbacks from request headers or signed gateway assertions.
/// The reference verifier currently accepts integer-only operation schemas; see NipInvocationJson.
/// </summary>
public sealed record NipInvocationVerificationOptions
{
    /// <summary>Current local opt-in; withdrawal is rechecked before replay admission.</summary>
    public required Func<bool> Enabled { get; init; }
    /// <summary>Exact logical receiving service ID, established by authenticated host routing.</summary>
    public required string Audience { get; init; }
    /// <summary>Live allowed issuer set from independently authenticated TLS/mTLS peer identity.</summary>
    public required Func<IReadOnlySet<string>> ChannelIssuers { get; init; }
    /// <summary>Exact features admitted on this hop; discovery is insufficient.</summary>
    public required IReadOnlyList<FeatureSelection> SelectedFeatures { get; init; }
    /// <summary>Receiver ceiling for signed capabilities; required operation capability is checked by Authorize.</summary>
    public required IReadOnlySet<string> AllowedCapabilities { get; init; }
    /// <summary>Receiver-owned vocabulary/ceiling for exact deployment scope identifiers.</summary>
    public required IReadOnlySet<string> AllowedScopes { get; init; }
    /// <summary>Maximum permitted assurance assertion: anonymous, attested or verified.</summary>
    public required string MaximumAssurance { get; init; }
    /// <summary>Live configured Ed25519 key lookup; revoked/unknown issuer keys return null.</summary>
    public required Func<string, string, CancellationToken, ValueTask<PublicKey?>> ResolveKey { get; init; }
    /// <summary>Pure registered operation validation; it must not access/modify business state or consume replay.</summary>
    public required Func<JsonElement, CancellationToken, ValueTask<bool>> ValidateOperation { get; init; }
    /// <summary>Live subject/action/node policy, including every capability/scope the operation requires.</summary>
    public required Func<NipInvocationClaims, JsonElement, CancellationToken, ValueTask<bool>> Authorize { get; init; }
    /// <summary>Mandatory qualified durable replay storage; no default in-memory adapter exists.</summary>
    public required INipProjectionReplayStore ReplayStore { get; init; }
    /// <summary>Trusted clock; never a request timestamp.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Skew allowance, 0..5 seconds; storage still retains until expires_at+5.</summary>
    public int ClockSkewSeconds { get; init; } = 5;
}

/// <summary>
/// An admitted projection bound to a single host invocation object. Not serializable authority.
/// Revalidation can only continue that same request; a new request needs a fresh projection.
/// </summary>
public sealed class NipAdmittedInvocation
{
    internal NipAdmittedInvocation(NipInvocationClaims claims, object invocation, string carrier, string digest)
        => (Claims, Invocation, Carrier, Digest) = (claims, invocation, carrier, digest);
    /// <summary>Verified gateway assertions after receiver policy and atomic replay admission.</summary>
    public NipInvocationClaims Claims { get; }
    internal object Invocation { get; }
    internal string Carrier { get; }
    internal string Digest { get; }
}

/// <summary>
/// Experimental NIP §7.8 verifier using platform Ed25519 and the integer-only reference
/// operation schema. It never authenticates a transport peer or advertises a feature.
/// A valid signed envelope cannot substitute for the independently authenticated channel.
/// </summary>
public static class NipInvocationVerifier
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] PayloadFields = ["profile", "revision", "issuer", "key_id", "audience",
        "subject", "assurance", "capabilities", "scope", "issued_at", "expires_at", "projection_id", "request_digest"];

    /// <summary>
    /// Validate schema/channel/key/signature/time/request/policy, then atomically claim replay.
    /// Invocation is an unshared host request object (for example HttpContext), never a wire ID.
    /// Invalid operations do not consume replay. Cancellation propagates; infrastructure fails closed.
    /// </summary>
    public static async ValueTask<NipAdmittedInvocation> AdmitAsync(IReadOnlyList<string> carriers,
        JsonElement logicalRequest, object invocation, NipInvocationVerificationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var (claim, carrier, digest) = await VerifyAsync(carriers, logicalRequest, options, ct).ConfigureAwait(false);
        NipProjectionReplayOutcome outcome;
        try
        {
            outcome = await options.ReplayStore.ClaimAsync(claim.Audience, claim.Issuer, claim.ProjectionId,
                checked(claim.ExpiresAt + options.ClockSkewSeconds), checked(claim.ExpiresAt + 5), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Fail("VERIFIER-UNAVAILABLE"); }
        if (outcome == NipProjectionReplayOutcome.Duplicate) throw Fail("REPLAY");
        if (outcome == NipProjectionReplayOutcome.Expired) throw Fail("EXPIRED");
        if (outcome != NipProjectionReplayOutcome.Claimed) throw Fail("VERIFIER-UNAVAILABLE");
        return new(claim, invocation, carrier, digest);
    }

    /// <summary>
    /// Recheck a previously admitted projection immediately before execution, without claiming
    /// it twice. Requires the SAME host invocation object, carrier and request digest. This is
    /// not admission for another request and cannot authorize a new transport invocation.
    /// </summary>
    public static async ValueTask RevalidateAsync(NipAdmittedInvocation admitted, IReadOnlyList<string> carriers,
        JsonElement logicalRequest, object invocation, NipInvocationVerificationOptions options, CancellationToken ct = default)
    {
        if (!ReferenceEquals(admitted.Invocation, invocation) || carriers.Count != 1
            || carriers[0] != admitted.Carrier || NipInvocationJson.RequestDigest(logicalRequest) != admitted.Digest)
            throw Fail("BINDING-MISMATCH");
        var (claim, _, _) = await VerifyAsync(carriers, logicalRequest, options, ct).ConfigureAwait(false);
        if (claim.ProjectionId != admitted.Claims.ProjectionId || claim.Issuer != admitted.Claims.Issuer
            || claim.Subject != admitted.Claims.Subject) throw Fail("BINDING-MISMATCH");
    }

    private static async ValueTask<(NipInvocationClaims Claim, string Carrier, string Digest)> VerifyAsync(
        IReadOnlyList<string> carriers, JsonElement request, NipInvocationVerificationOptions options, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!options.Enabled()) throw Fail("UNTRUSTED");
            if (options.ClockSkewSeconds is < 0 or > 5) throw Fail("VERIFIER-UNAVAILABLE");
            if (Level(options.MaximumAssurance) < 0) throw Fail("VERIFIER-UNAVAILABLE");
            if (carriers.Count != 1) throw Fail("INVALID");
            var envelopeBytes = Decode(carriers[0], 21846);
            if (envelopeBytes.Length > 16384) throw Fail("INVALID");
            var envelope = NipInvocationJson.Parse(Utf8.GetString(envelopeBytes), 16384);
            Members(envelope, ["payload", "signature"]);
            var payload = envelope.GetProperty("payload");
            var claim = ParseClaims(payload);
            var signature = Decode(Text(envelope, "signature", 86), 86);
            if (signature.Length != 64) throw Fail("INVALID");
            var signed = "NPS-CONTEXT-PROJECTION/1\0"u8.ToArray().Concat(NipInvocationJson.Canonicalize(payload)).ToArray();
            var digest = NipInvocationJson.RequestDigest(request);
            for (var pass = 0; pass < 2; pass++)
            {
                if (!options.Enabled() || !ExactContains(options.ChannelIssuers(), claim.Issuer)) throw Fail("UNTRUSTED");
                var key = await options.ResolveKey(claim.Issuer, claim.KeyId, ct).ConfigureAwait(false);
                if (key is null || !SignatureMatches(key, signed, signature)) throw Fail("UNTRUSTED");
                if (!options.Enabled() || !ExactContains(options.ChannelIssuers(), claim.Issuer)) throw Fail("UNTRUSTED");
                if (claim.Audience != options.Audience || !WithinAssurance(claim.Assurance, options.MaximumAssurance)
                    || claim.Capabilities.Any(capability => !ExactContains(options.AllowedCapabilities, capability))
                    || claim.Scope.Any(scope => !ExactContains(options.AllowedScopes, scope))) throw Fail("BINDING-MISMATCH");
                ValidateRequest(request, options.SelectedFeatures);
                if (Text(request, "target", 512) != options.Audience) throw Fail("BINDING-MISMATCH");
                if (claim.RequestDigest != digest) throw Fail("BINDING-MISMATCH");
                var now = options.TimeProvider.GetUtcNow().ToUnixTimeSeconds();
                if (now < 0) throw Fail("VERIFIER-UNAVAILABLE");
                if (claim.IssuedAt > now + options.ClockSkewSeconds) throw Fail("INVALID");
                if (now >= claim.ExpiresAt + options.ClockSkewSeconds) throw Fail("EXPIRED");
                if (pass == 0 && !await options.ValidateOperation(request, ct).ConfigureAwait(false)) throw Fail("BINDING-MISMATCH");
                if (pass == 0 && !await options.Authorize(claim, request, ct).ConfigureAwait(false)) throw Fail("BINDING-MISMATCH");
                if (!options.Enabled() || !ExactContains(options.ChannelIssuers(), claim.Issuer)) throw Fail("UNTRUSTED");
                now = options.TimeProvider.GetUtcNow().ToUnixTimeSeconds();
                if (now >= claim.ExpiresAt + options.ClockSkewSeconds) throw Fail("EXPIRED");
            }
            return (claim, carriers[0], digest);
        }
        catch (NipInvocationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or EncoderFallbackException or FormatException)
        { throw Fail("INVALID"); }
        catch { throw Fail("VERIFIER-UNAVAILABLE"); }
    }

    internal static NipInvocationClaims ParseClaims(JsonElement payload)
    {
        Members(payload, PayloadFields);
        if (Text(payload, "profile", 128) != "nps.invocation_context" || Integer(payload, "revision") != 1) throw Fail("INVALID");
        var issuer = Text(payload, "issuer", 512);
        var subject = Text(payload, "subject", 512);
        if (!Nid(issuer) || !Nid(subject)) throw Fail("INVALID");
        var key = Text(payload, "key_id", 128);
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._:-]{1,128}\z", RegexOptions.NonBacktracking)) throw Fail("INVALID");
        var issued = Integer(payload, "issued_at");
        var expires = Integer(payload, "expires_at");
        if (expires <= issued || expires - issued > 60 || expires > 9_007_199_254_740_986) throw Fail("INVALID");
        var projection = Text(payload, "projection_id", 32);
        var digest = Text(payload, "request_digest", 71);
        if (!Regex.IsMatch(projection, @"\A[0-9a-f]{32}\z", RegexOptions.NonBacktracking)
            || !Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.NonBacktracking)) throw Fail("INVALID");
        var assurance = Text(payload, "assurance", 16);
        if (Level(assurance) < 0) throw Fail("INVALID");
        return new(issuer, key, Text(payload, "audience", 512), subject, assurance,
            Strings(payload.GetProperty("capabilities"), 128), Strings(payload.GetProperty("scope"), 256),
            issued, expires, projection, digest);
    }

    internal static void ValidateRequest(JsonElement request, IReadOnlyList<FeatureSelection> selected)
    {
        Members(request, ["protocol", "operation", "target", "idempotency_key", "required_features", "body"]);
        var protocol = Text(request, "protocol", 32);
        if (protocol.Any(character => character is < '!' or > '~')) throw Fail("INVALID");
        _ = Text(request, "operation", 256);
        _ = Text(request, "target", 512);
        var key = request.GetProperty("idempotency_key");
        if (key.ValueKind != JsonValueKind.Null) _ = Text(request, "idempotency_key", 128);
        var requirements = request.GetProperty("required_features");
        if (requirements.ValueKind != JsonValueKind.Array || requirements.GetArrayLength() > 32) throw Fail("INVALID");
        var encoded = string.Join(',', requirements.EnumerateArray().Select(pair =>
        {
            Members(pair, ["id", "revision"]);
            return Text(pair, "id", 96) + "=" + Integer(pair, "revision").ToString(System.Globalization.CultureInfo.InvariantCulture);
        }));
        IReadOnlyList<FeatureSelection> pairs;
        try { pairs = FeatureWireParser.ParseRequiredHeader([encoded]); }
        catch (FeatureFormatException) { throw Fail("INVALID"); }
        if (!pairs.Any(pair => pair.Id == "nps.invocation_context" && pair.Revision == 1)
            || pairs.Any(pair => !selected.Contains(pair))) throw Fail("BINDING-MISMATCH");
        if (pairs.Any(pair => pair.Id == "nps.governed_challenge")
            && !pairs.Any(pair => pair.Id == "nps.production_runtime" && pair.Revision == 1)) throw Fail("BINDING-MISMATCH");
        var body = request.GetProperty("body");
        Members(body, ["encoding", "value"]);
        if (Text(body, "encoding", 16) != "json") throw Fail("INVALID"); // No byte-body profile is registered here.
    }

    private static void Members(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Fail("INVALID");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Count || names.Any(name => !expected.Contains(name, StringComparer.Ordinal))) throw Fail("INVALID");
    }

    private static string Text(JsonElement value, string field, int maximum)
    {
        if (!value.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String) throw Fail("INVALID");
        var text = property.GetString()!;
        if (Utf8.GetByteCount(text) is < 1 || Utf8.GetByteCount(text) > maximum) throw Fail("INVALID");
        return text;
    }

    private static long Integer(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var number) || number is < 0 or > 9_007_199_254_740_991
            || property.GetRawText().Any(character => character is < '0' or > '9')) throw Fail("INVALID");
        return number;
    }

    private static IReadOnlyList<string> Strings(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 64) throw Fail("INVALID");
        var strings = new List<string>();
        byte[]? previous = null;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Fail("INVALID");
            var text = item.GetString()!;
            var bytes = Utf8.GetBytes(text);
            if (bytes.Length is < 1 || bytes.Length > maximum
                || previous is not null && previous.AsSpan().SequenceCompareTo(bytes) >= 0) throw Fail("INVALID");
            previous = bytes;
            strings.Add(text);
        }
        return strings.AsReadOnly();
    }

    private static byte[] Decode(string value, int maximum)
    {
        if (value.Length is 0 || value.Length > maximum
            || !Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+\z", RegexOptions.NonBacktracking)) throw Fail("INVALID");
        var padded = value.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
        if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != value) throw Fail("INVALID");
        return bytes;
    }

    private static bool Nid(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is not (4 or 5) || parts[0] != "urn" || parts[1] != "nps"
            || parts[2] is not ("agent" or "node" or "org") || parts.Length == 4 && parts[2] != "org"
            || parts.Length == 5 && !Regex.IsMatch(parts[4], @"\A[A-Za-z0-9_.-]+\z", RegexOptions.NonBacktracking)) return false;
        var domain = parts[3];
        return domain.Length <= 253 && domain.Split('.').All(label => label.Length is >= 1 and <= 63
            && Regex.IsMatch(label, @"\A[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z", RegexOptions.NonBacktracking));
    }
    private static int Level(string assurance) => assurance switch { "anonymous" => 0, "attested" => 1, "verified" => 2, _ => -1 };
    private static bool WithinAssurance(string asserted, string maximum) => Level(maximum) >= 0 && Level(asserted) <= Level(maximum);
    private static bool ExactContains(IEnumerable<string> values, string expected) =>
        values.Any(value => string.Equals(value, expected, StringComparison.Ordinal));
    private static bool SignatureMatches(PublicKey key, byte[] data, byte[] signature)
    {
        try { return SignatureAlgorithm.Ed25519.Verify(key, data, signature); }
        catch (ArgumentException) { return false; }
    }
    private static NipInvocationException Fail(string suffix) => new("NIP-CONTEXT-" + suffix);
}
