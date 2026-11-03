// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NPS.Core.Alpha20;
using NPS.NIP.Invocation;
using NSec.Cryptography;

namespace NPS.Tests.Alpha20;

/// <summary>Projector schema/policy/race tests; real channel and CA cases live in ResultInvocationTlsTests.</summary>
public sealed class NipResultHttpProjectorTests
{
    private const string Target = "urn:nps:node:fixture:results";
    private static readonly FeatureSelection[] Features = [new("nps.data_results", 1), new("nps.invocation_context", 1)];
    private static string Frame => JsonSerializer.Serialize(new { action_id = "result.create", idempotency_key = Guid.NewGuid(), @params = new { source_id = "orders" } });

    [Theory]
    [InlineData("http", "UNTRUSTED")]
    [InlineData("target", "BINDING-MISMATCH")]
    [InlineData("features", "BINDING-MISMATCH")]
    [InlineData("scope", "BINDING-MISMATCH")]
    [InlineData("capability", "BINDING-MISMATCH")]
    [InlineData("duplicate", "INVALID")]
    [InlineData("subject", "UNTRUSTED")]
    [InlineData("expiry", "EXPIRED")]
    [InlineData("key", "UNTRUSTED")]
    [InlineData("withdrawal", "UNTRUSTED")]
    [InlineData("unavailable", "VERIFIER-UNAVAILABLE")]
    public async Task NoCarrierIsReturnedForUntrustedInvalidOrWithdrawnAuthority(string failure, string suffix)
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519);
        var context = new DefaultHttpContext();
        context.Request.Scheme = failure == "http" ? "http" : "https";
        context.Request.Headers["X-NPS-Invocation-Context"] = "untrusted-public-input";
        var admissions = 0;
        var lookups = 0;
        var enabled = true;
        var projector = new NipResultHttpProjector(new NipResultHttpProjectorOptions
        {
            Enabled = () => enabled, Issuer = "urn:nps:node:fixture:gateway", Audience = Target, KeyId = "gateway-key",
            AllowedCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nwp:action" },
            AllowedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "results:access" },
            ResolveSigningKey = _ => { lookups++; return ValueTask.FromResult<Key?>(failure == "key" && lookups == 2 ? null : key); },
            Authenticate = (_, _, _) =>
            {
                Assert.False(context.Request.Headers.ContainsKey("X-NPS-Invocation-Context"));
                admissions++;
                if (failure == "unavailable") throw new IOException("private issuer error");
                if (failure == "withdrawal" && admissions == 2) enabled = false;
                return ValueTask.FromResult<NipGatewayAuthority?>(new(
                    failure == "subject" && admissions == 2 ? "urn:nps:agent:fixture:bob" : "urn:nps:agent:fixture:alice",
                    "attested", failure == "capability" ? ["NWP:ACTION"] : failure == "duplicate" ? ["nwp:action", "nwp:action"] : ["nwp:action"],
                    failure == "scope" ? ["RESULTS:ACCESS"] : ["results:access"],
                    DateTimeOffset.UtcNow.AddSeconds(failure == "expiry" ? -1 : 60).ToUnixTimeSeconds()));
            },
        });
        var error = await Assert.ThrowsAsync<NipInvocationException>(async () => await projector.ProjectAsync(context, Frame,
            failure == "target" ? "urn:nps:node:fixture:other" : Target, failure == "features" ? [Features[0]] : Features));
        Assert.Equal("NIP-CONTEXT-" + suffix, error.Code);
        Assert.False(context.Request.Headers.ContainsKey("X-NPS-Invocation-Context"));
        Assert.DoesNotContain("private issuer", error.Message);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeSigning()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519);
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        var projector = new NipResultHttpProjector(new NipResultHttpProjectorOptions
        {
            Enabled = () => true, Issuer = "urn:nps:node:fixture:gateway", Audience = Target, KeyId = "gateway-key",
            AllowedCapabilities = new HashSet<string> { "nwp:action" }, AllowedScopes = new HashSet<string> { "results:access" },
            ResolveSigningKey = _ => ValueTask.FromResult<Key?>(key),
            Authenticate = (_, _, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<NipGatewayAuthority?>(new("urn:nps:agent:fixture:alice", "attested",
                    ["nwp:action"], ["results:access"], DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds()));
            },
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await projector.ProjectAsync(context, Frame, Target, Features, cancellation.Token));
    }
}
