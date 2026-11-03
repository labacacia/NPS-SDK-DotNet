// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using NPS.NDP;
using NPS.NDP.Discovery;
using NPS.NDP.Registry;
using NPS.NWP.Results;
using NPS.Tests.Ndp;

namespace NPS.Tests.Nwp;

// Reference registry state is local and volatile. Both distinct result nodes expose
// actual HTTPS/mTLS gateways and receivers with independent identities/stores/NWM.
public sealed partial class ResultInvocationTlsTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task NdpTwoNodeTransferRequiresFreshAdmissionAndCannotMigrateOldResult(bool sameIssuer)
    {
        var successor = new ResultInvocationTlsTests { ReferenceNodeId = sameIssuer ? ReferenceNodeId : "urn:nps:node:fixture:successor" };
        await successor.InitializeAsync();
        try
        {
            using var publisherA = new NdpRouteTestPublisher(ReferenceNodeId);
            using var publisherB = new NdpRouteTestPublisher(successor.ReferenceNodeId);
            var now = DateTimeOffset.UtcNow;
            var endpointA = new Uri(_endpoint, "/gateway");
            var endpointB = new Uri(successor._endpoint, "/gateway");
            var authorityA = publisherA.Authority(now, endpoint: endpointA) with
            { TlsPin = _serverCertificate.GetCertHashString(HashAlgorithmName.SHA256) };
            var epochB = sameIssuer ? 1UL : 2UL;
            var authorityB = publisherB.Authority(now, epoch: epochB, endpoint: endpointB) with
            { TlsPin = successor._serverCertificate.GetCertHashString(HashAlgorithmName.SHA256) };
            var policies = new Dictionary<string, NdpRouteAuthority> { [ReferenceNodeId] = authorityA };
            var registry = new NdpResultDiscovery("fixture-reference-registry", nid => policies.GetValueOrDefault(nid));
            Assert.Equal(NdpRegistryDecision.Accepted, registry.Admit(publisherA.Announce(now, endpoint: endpointA)).Decision);
            var selectedA = registry.Resolve("orders-cluster", "data-results");
            using var ownerA = DiscoveryClient(selectedA);
            registry.ValidateCurrent(selectedA, "data-results");
            var retained = await ownerA.CreateAsync("orders", Guid.NewGuid());
            registry.ValidateCurrent(selectedA, "data-results");
            Assert.Equal(2, (await ownerA.ReadAsync(retained, 5)).Rows.Count);
            Assert.Equal(1, _captures);
            Assert.Equal(2, _gatewayRequests);

            // Independent current ownership authorization is required before new fences.
            Assert.Equal(NdpErrorCodes.IssuerNotAllowed,
                registry.Admit(publisherB.Announce(now, epoch: 999, endpoint: endpointB)).ErrorCode);
            Assert.Equal(0, successor._gatewayRequests);
            policies.Remove(ReferenceNodeId);
            policies[successor.ReferenceNodeId] = authorityB;
            Assert.Equal(NdpRegistryDecision.Accepted,
                registry.Admit(publisherB.Announce(now, sequence: 2, epoch: epochB, endpoint: endpointB)).Decision);
            var selectedB = registry.Resolve("orders-cluster", "data-results");
            Assert.Equal(sameIssuer, selectedA.Nid == selectedB.Nid);
            Assert.NotEqual(selectedA.Endpoint, selectedB.Endpoint);
            Assert.NotEqual(selectedA.TlsPin, selectedB.TlsPin);
            Assert.Equal(NdpErrorCodes.StateUnavailable,
                Assert.Throws<NdpDiscoveryException>(() => registry.ValidateCurrent(selectedA, "data-results")).Code);
            Assert.Equal(0, successor._gatewayRequests); // Old binding rejected before any B dispatch.
            Assert.Equal(0, successor._captures); // No automatic create/replay after transfer.

            using var ownerB = successor.DiscoveryClient(selectedB); // New TLS client, NWM and NIP admission.
            registry.ValidateCurrent(selectedB, "data-results");
            var unavailable = await Assert.ThrowsAsync<ResultHttpException>(() => ownerB.ReadAsync(retained, 5));
            Assert.False(unavailable.OutcomeUnknown);
            Assert.Equal(sameIssuer ? 1 : 0, successor._gatewayRequests); // Same NID reaches empty successor; different issuer rejects locally.
            if (sameIssuer) Assert.Equal("NWP-RESULT-NOT-FOUND", unavailable.ErrorCode);
            Assert.Equal(0, successor._captures);
            // An explicit NEW owner operation may create new state; it does not recover the old result.
            registry.ValidateCurrent(selectedB, "data-results");
            var replacement = await ownerB.CreateAsync("orders", Guid.NewGuid());
            Assert.NotEqual(retained.ResultRef, replacement.ResultRef);
            Assert.Equal(successor.ReferenceNodeId, replacement.IssuerNid);
            registry.ValidateCurrent(selectedB, "data-results");
            Assert.Equal(2, (await ownerB.ReadAsync(replacement, 5)).Rows.Count);
            Assert.Equal(sameIssuer ? 3 : 2, successor._gatewayRequests);
            Assert.Equal(sameIssuer ? 6 : 4, successor._gatewayAdmissions);
            Assert.Equal(sameIssuer ? 3 : 2, successor._mintedProofs.Distinct().Count());
            Assert.Equal(1, successor._captures);
            registry.ValidateCurrent(selectedB, "data-results");
            await ownerB.ReleaseAsync(replacement);
            // Static explicitly configured A remains usable independently of registry routing.
            using var staticA = PublicClient();
            Assert.Equal(2, (await staticA.ReadAsync(retained, 5)).Rows.Count);
            await staticA.ReleaseAsync(retained);
        }
        finally { await successor.DisposeAsync(); }
    }

    [Fact]
    public async Task NdpCoarseCapabilityCannotBypassWithdrawnNwmOrCurrentNip()
    {
        using var publisher = new NdpRouteTestPublisher(ReferenceNodeId);
        var now = DateTimeOffset.UtcNow;
        var endpoint = new Uri(_endpoint, "/gateway");
        var authority = publisher.Authority(now, endpoint: endpoint) with
        { TlsPin = _serverCertificate.GetCertHashString(HashAlgorithmName.SHA256) };
        var registry = new NdpResultDiscovery("fixture-reference-registry", _ => authority);
        Assert.Equal(NdpRegistryDecision.Accepted, registry.Admit(publisher.Announce(now, endpoint: endpoint)).Decision);
        var selection = registry.Resolve("orders-cluster", "data-results");
        using var owner = DiscoveryClient(selection);
        registry.ValidateCurrent(selection, "data-results");
        var prime = await owner.CreateAsync("orders", Guid.NewGuid());
        registry.ValidateCurrent(selection, "data-results");
        await owner.ReleaseAsync(prime);
        Assert.Equal(2, _gatewayRequests);
        Assert.Equal(2, _handlers);
        Assert.Equal(1, _captures);
        _gatewayEnabled = false;
        _manifestVersion++;
        registry.ValidateCurrent(selection, "data-results");
        var unsupported = await Assert.ThrowsAsync<ResultHttpException>(() => owner.CreateAsync("orders", Guid.NewGuid()));
        Assert.False(unsupported.OutcomeUnknown);
        Assert.Equal(2, _gatewayRequests); // No additional POST after cached affirmative support is withdrawn.
        Assert.Equal(2, _handlers);
        Assert.Equal(1, _captures);
        _gatewayEnabled = true;
        _manifestVersion++;
        await _identityCaStore.RevokeAsync(Alice, "D07 current identity withdrawal", DateTime.UtcNow);
        registry.ValidateCurrent(selection, "data-results");
        var revoked = await Assert.ThrowsAsync<ResultHttpException>(() => owner.CreateAsync("orders", Guid.NewGuid()));
        Assert.False(revoked.OutcomeUnknown);
        Assert.Equal(3, _gatewayRequests);
        Assert.Equal(2, _handlers); // No additional receiver/provider entry after current NIP withdrawal.
        Assert.Equal(1, _captures);
    }

    [Fact]
    public async Task NdpIssuerLostStateIsUnavailableWithoutRecreatingOrReplaying()
    {
        using var publisher = new NdpRouteTestPublisher(ReferenceNodeId);
        var now = DateTimeOffset.UtcNow;
        var endpoint = new Uri(_endpoint, "/gateway");
        var authority = publisher.Authority(now, endpoint: endpoint) with
        { TlsPin = _serverCertificate.GetCertHashString(HashAlgorithmName.SHA256) };
        var registry = new NdpResultDiscovery("fixture-reference-registry", _ => authority);
        registry.Admit(publisher.Announce(now, endpoint: endpoint));
        var selection = registry.Resolve("orders-cluster", "data-results");
        using var owner = DiscoveryClient(selection);
        registry.ValidateCurrent(selection, "data-results");
        var descriptor = await owner.CreateAsync("orders", Guid.NewGuid());
        registry.ValidateCurrent(selection, "data-results");
        Assert.True((await owner.ReleaseAsync(descriptor)).Released); // Explicitly remove issuer-local state.
        registry.ValidateCurrent(selection, "data-results");
        var lost = await Assert.ThrowsAsync<ResultHttpException>(() => owner.ReadAsync(descriptor, 5));
        Assert.Equal("NWP-RESULT-NOT-FOUND", lost.ErrorCode);
        Assert.False(lost.OutcomeUnknown);
        Assert.Equal(1, _captures); // No synthetic recreate on a missing result.
        Assert.Equal(3, _gatewayRequests);
    }

    private ResultHttpClient DiscoveryClient(NdpRouteSelection route)
    {
        Assert.Equal(ReferenceNodeId, route.Nid);
        Assert.Equal(new Uri(_endpoint, "/gateway"), route.Endpoint);
        // Configure actual transport from the selected host policy, not from announcement hints.
        return new(route.Endpoint, route.Nid, configureTransport: handler =>
        {
            ConfigureTls(handler, _ownerCertificate);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null
                && certificate.GetCertHashString(HashAlgorithmName.SHA256) == route.TlsPin;
        });
    }
}
