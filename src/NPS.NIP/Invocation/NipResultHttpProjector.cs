// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NPS.Core.Alpha20;
using NSec.Cryptography;

namespace NPS.NIP.Invocation;

/// <summary>
/// Server-owned authority returned only after authenticating the actual public connection,
/// verifying current NIP/capability/node/action/revocation policy and narrowing delegation.
/// Never deserialize this object from a public body/header. Expiry bounds the emitted proof.
/// </summary>
public sealed record NipGatewayAuthority(string Subject, string Assurance,
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Scope, long ExpiresAt);

/// <summary>
/// Explicit gateway configuration. The host owns admission, live key lifetime and route
/// qualification; no incoming issuer/key/tenant hint may select these values.
/// </summary>
public sealed record NipResultHttpProjectorOptions
{
    /// <summary>Current gateway/route opt-in; checked again immediately before signing.</summary>
    public required Func<bool> Enabled { get; init; }
    /// <summary>Gateway's independently authenticated outgoing transport identity.</summary>
    public required string Issuer { get; init; }
    /// <summary>Configured receiving node, never a caller-selected target URL or NID.</summary>
    public required string Audience { get; init; }
    /// <summary>Receiver-configured identifier of the live gateway signing key.</summary>
    public required string KeyId { get; init; }
    /// <summary>Live key lookup. Null means withdrawn. Host retains ownership/disposal of keys.</summary>
    public required Func<CancellationToken, ValueTask<Key?>> ResolveSigningKey { get; init; }
    /// <summary>
    /// Authenticate original HttpContext and exact outgoing logical request, including current
    /// NIP revocation and operation/scope policy. Runs twice across key resolution; return null
    /// on denial. Projection headers have already been stripped. X-NWP-Agent is not identity.
    /// </summary>
    public required Func<HttpContext, JsonElement, CancellationToken, ValueTask<NipGatewayAuthority?>> Authenticate { get; init; }
    /// <summary>Exact receiver/deployment capability ceiling; authentication may only narrow it.</summary>
    public required IReadOnlySet<string> AllowedCapabilities { get; init; }
    /// <summary>Exact receiver/deployment scope vocabulary; authentication may only narrow it.</summary>
    public required IReadOnlySet<string> AllowedScopes { get; init; }
    /// <summary>Trusted signing clock. No request timestamp is used.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Proof lifetime in seconds, 1..60 and never past admitted authority expiry.</summary>
    public int LifetimeSeconds { get; init; } = 60;
}

/// <summary>
/// Creates fresh NIP §7.8 projections for the integer-only result HTTP leg after real
/// public-connection admission. It never forwards incoming projections or installs endpoints,
/// authenticates callers from hints, advertises support, or retries business operations.
/// </summary>
public sealed class NipResultHttpProjector(NipResultHttpProjectorOptions options)
{
    private static readonly FeatureSelection[] Features = [new("nps.data_results", 1), new("nps.invocation_context", 1)];

    /// <summary>
    /// Strip public projection headers, bind the exact outgoing body/target/features, admit
    /// current caller authority and sign a fresh one-hop proof. The caller must already enforce
    /// public-leg feature admission and validate/limit the original request before forwarding.
    /// Cancellation propagates. Denied/unavailable admission never returns a usable carrier.
    /// </summary>
    public async ValueTask<string> ProjectAsync(HttpContext originalConnection, string outgoingFrame,
        string target, IReadOnlyList<FeatureSelection> requirements, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(originalConnection);
            originalConnection.Request.Headers.Remove("X-NPS-Invocation-Context");
            if (!options.Enabled() || !originalConnection.Request.IsHttps) throw Fail("UNTRUSTED");
            if (target != options.Audience || !requirements.SequenceEqual(Features)) throw Fail("BINDING-MISMATCH");
            if (options.LifetimeSeconds is < 1 or > 60) throw Fail("VERIFIER-UNAVAILABLE");
            var request = NipInvocationJson.ResultActionRequest(outgoingFrame, target, requirements);
            NipInvocationVerifier.ValidateRequest(request, requirements);
            var first = await options.Authenticate(originalConnection, request, ct).ConfigureAwait(false);
            if (first is null) throw Fail("UNTRUSTED");
            var key = await options.ResolveSigningKey(ct).ConfigureAwait(false);
            if (key is null) throw Fail("UNTRUSTED");
            var authority = await options.Authenticate(originalConnection, request, ct).ConfigureAwait(false);
            if (authority is null || authority.Subject != first.Subject) throw Fail("UNTRUSTED");
            if (authority.Capabilities.Any(value => !Exact(options.AllowedCapabilities, value))
                || authority.Scope.Any(value => !Exact(options.AllowedScopes, value))
                || !Exact(authority.Capabilities, "nwp:action")) throw Fail("BINDING-MISMATCH");
            var now = options.TimeProvider.GetUtcNow().ToUnixTimeSeconds();
            var expires = Math.Min(checked(now + options.LifetimeSeconds), authority.ExpiresAt);
            if (now < 0 || expires <= now) throw Fail("EXPIRED");
            var payload = JsonSerializer.SerializeToElement(new
            {
                profile = "nps.invocation_context", revision = 1, issuer = options.Issuer, key_id = options.KeyId,
                audience = target, subject = authority.Subject, assurance = authority.Assurance,
                capabilities = Sorted(authority.Capabilities), scope = Sorted(authority.Scope),
                issued_at = now, expires_at = expires,
                projection_id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                request_digest = NipInvocationJson.RequestDigest(request),
            });
            _ = NipInvocationVerifier.ParseClaims(payload);
            var bytes = "NPS-CONTEXT-PROJECTION/1\0"u8.ToArray().Concat(NipInvocationJson.Canonicalize(payload)).ToArray();
            ct.ThrowIfCancellationRequested();
            if (!options.Enabled()) throw Fail("UNTRUSTED");
            // Resolve again so key withdrawal during asynchronous caller admission cannot sign.
            if (!ReferenceEquals(key, await options.ResolveSigningKey(ct).ConfigureAwait(false))) throw Fail("UNTRUSTED");
            ct.ThrowIfCancellationRequested();
            if (!options.Enabled()) throw Fail("UNTRUSTED");
            var signature = SignatureAlgorithm.Ed25519.Sign(key, bytes);
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new { payload, signature = Encode(signature) });
            if (envelope.Length > 16384) throw Fail("INVALID");
            return Encode(envelope);
        }
        catch (NipInvocationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw Fail("VERIFIER-UNAVAILABLE"); }
    }

    private static string[] Sorted(IReadOnlyList<string> values)
    {
        if (values.Count > 64 || values.Distinct(StringComparer.Ordinal).Count() != values.Count) throw Fail("INVALID");
        return values.OrderBy(value => Encoding.UTF8.GetBytes(value), Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right))).ToArray();
    }
    private static bool Exact(IEnumerable<string> values, string expected) => values.Any(value => string.Equals(value, expected, StringComparison.Ordinal));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static NipInvocationException Fail(string suffix) => new("NIP-CONTEXT-" + suffix);
}
