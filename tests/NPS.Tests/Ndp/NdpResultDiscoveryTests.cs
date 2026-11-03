// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using NPS.NDP;
using NPS.NDP.Discovery;
using NPS.NDP.Registry;

namespace NPS.Tests.Ndp;

public sealed class NdpResultDiscoveryTests : IDisposable
{
    private readonly NdpRouteTestClock _clock = new();
    private readonly NdpRouteTestPublisher _a = new("urn:nps:node:fixture:discovery-a");
    private readonly NdpRouteTestPublisher _b = new("urn:nps:node:fixture:discovery-b");
    private readonly Dictionary<string, NdpRouteAuthority> _authority = new(StringComparer.Ordinal);
    private readonly NdpResultDiscovery _registry;
    private DateTimeOffset Now => _clock.GetUtcNow();
    public NdpResultDiscoveryTests()
    {
        _authority[_a.Nid] = _a.Authority(Now);
        _registry = NewRegistry();
    }
    private NdpResultDiscovery NewRegistry(int capacity = 256) => new("fixture-registry",
        nid => _authority.GetValueOrDefault(nid), _clock, capacity);
    private static void Code(string expected, Action action) => Assert.Equal(expected, Assert.Throws<NdpDiscoveryException>(action).Code);
    private static void Accepted(NdpRegistryAdmission admission) => Assert.Equal(NdpRegistryDecision.Accepted, admission.Decision);

    [Fact]
    public void StrictSignatureAndHostAuthorityPrecedeAnyFenceMutation()
    {
        Assert.Equal(NdpErrorCodes.IssuerNotAllowed, _registry.Admit(_b.Announce(Now, epoch: 999)).ErrorCode);
        Assert.Equal(NdpErrorCodes.AnnounceSignatureInvalid, _registry.Admit(_a.Announce(Now, badSignature: true)).ErrorCode);
        Assert.Equal(NdpErrorCodes.IssuerNotAllowed, _registry.Admit(_a.Announce(Now, cluster: "attacker-cluster")).ErrorCode);
        Assert.Equal(NdpErrorCodes.IssuerNotAllowed, _registry.Admit(_a.Announce(Now, endpoint: new("https://evil.invalid:8443/gateway"))).ErrorCode);
        Accepted(_registry.Admit(_a.Announce(Now)));
        _registry.ValidateCurrent(_registry.Resolve("orders-cluster", "data-results"), "data-results");
    }

    [Fact]
    public void InvalidHighEpochSignatureCannotBurnFloor()
    {
        _authority[_a.Nid] = _a.Authority(Now, 100);
        Assert.Equal(NdpErrorCodes.AnnounceSignatureInvalid, _registry.Admit(_a.Announce(Now, epoch: 100, badSignature: true)).ErrorCode);
        _authority[_a.Nid] = _a.Authority(Now);
        Accepted(_registry.Admit(_a.Announce(Now)));
    }

    [Theory]
    [InlineData("null")][InlineData("{}")] [InlineData("[]")]
    [InlineData("{\"frame\":\"0x30\",\"frame\":\"0x30\"}")]
    public void MalformedAnnouncementsFailClosed(string json) =>
        Assert.Equal(NdpErrorCodes.AnnounceProfileViolation, _registry.Admit(Encoding.UTF8.GetBytes(json)).ErrorCode);

    [Fact]
    public void OversizedAndDuplicateSignedMembersAreRejected()
    {
        Assert.Equal(NdpErrorCodes.AnnounceProfileViolation, _registry.Admit(new byte[65537]).ErrorCode);
        var json = Encoding.UTF8.GetString(_a.Announce(Now)).Replace("\"graph_seq\":1", "\"graph_seq\":1,\"graph_seq\":1", StringComparison.Ordinal);
        Assert.Equal(NdpErrorCodes.AnnounceProfileViolation, _registry.Admit(Encoding.UTF8.GetBytes(json)).ErrorCode);
        var duplicateUnsigned = Encoding.UTF8.GetString(_a.Announce(Now)).Replace("\"health\":null", "\"health\":\"healthy\",\"health\":null", StringComparison.Ordinal);
        Assert.Equal(NdpErrorCodes.AnnounceProfileViolation, _registry.Admit(Encoding.UTF8.GetBytes(duplicateUnsigned)).ErrorCode);
    }

    [Fact]
    public void SequenceRollbackAndSameSequenceDigestChangeCannotReplaceRoute()
    {
        Accepted(_registry.Admit(_a.Announce(Now, sequence: 2)));
        var saved = _registry.Resolve("orders-cluster");
        Assert.Equal(NdpErrorCodes.GraphSeqRollback, _registry.Admit(_a.Announce(Now)).ErrorCode);
        Assert.Equal(NdpErrorCodes.AnnounceConflict, _registry.Admit(_a.Announce(Now, sequence: 2, ttl: 121)).ErrorCode);
        _registry.ValidateCurrent(saved);
    }

    [Fact]
    public void UnsignedHeartbeatAndWallClockRollbackCannotExtendLease()
    {
        var bytes = _a.Announce(Now, ttl: 10);
        Accepted(_registry.Admit(bytes));
        var saved = _registry.Resolve("orders-cluster");
        _clock.Advance(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(-30));
        Assert.Equal(NdpRegistryDecision.Duplicate,
            _registry.Admit(NdpRouteTestPublisher.Advisory(bytes, lastSeen: Now.AddYears(1))).Decision);
        Assert.True(_registry.Resolve("orders-cluster").ExpiresAt <= saved.ExpiresAt);
        _clock.Advance(TimeSpan.FromSeconds(2), TimeSpan.Zero);
        Code(NdpErrorCodes.ResolveStale, () => _registry.Resolve("orders-cluster"));
    }

    [Fact]
    public void UnsignedDrainingIsStickyUntilHigherSignedSequence()
    {
        var bytes = _a.Announce(Now);
        Accepted(_registry.Admit(bytes));
        var saved = _registry.Resolve("orders-cluster");
        _registry.Admit(NdpRouteTestPublisher.Advisory(bytes, health: "draining"));
        _registry.Admit(NdpRouteTestPublisher.Advisory(bytes, health: "healthy"));
        Code(NdpErrorCodes.ResolveStale, () => _registry.ValidateCurrent(saved));
        Accepted(_registry.Admit(_a.Announce(Now, sequence: 2)));
        Code(NdpErrorCodes.StateUnavailable, () => _registry.ValidateCurrent(saved));
    }

    [Theory]
    [InlineData("offline")][InlineData("draining")][InlineData("expired")][InlineData("caps")][InlineData("identity")]
    public void HighestOwnerFailureNeverFallsBack(string failure)
    {
        Accepted(_registry.Admit(_a.Announce(Now, ttl: 900)));
        _authority[_b.Nid] = _b.Authority(Now, 2);
        _registry.Admit(_b.Announce(Now, epoch: 2, ttl: failure == "offline" ? 0u : 10u,
            health: failure == "draining" ? "draining" : null,
            capabilities: failure == "caps" ? [] : null));
        if (failure == "expired") _clock.Advance(TimeSpan.FromSeconds(11));
        if (failure == "identity") _authority.Remove(_b.Nid);
        Code(failure == "caps" ? NdpErrorCodes.ResolveNotFound : failure == "identity" ? NdpErrorCodes.IssuerNotAllowed : NdpErrorCodes.ResolveStale,
            () => _registry.Resolve("orders-cluster", "data-results"));
        Assert.Equal(NdpErrorCodes.GraphSeqRollback, _registry.Admit(_a.Announce(Now, sequence: 2)).ErrorCode);
    }

    [Fact]
    public void EqualEpochSplitRequiresHigherAuthorizedEpochEvenAfterExpiry()
    {
        Accepted(_registry.Admit(_a.Announce(Now, ttl: 900)));
        _authority[_b.Nid] = _b.Authority(Now);
        Accepted(_registry.Admit(_b.Announce(Now, ttl: 10)));
        Code(NdpErrorCodes.ClusterSplit, () => _registry.Resolve("orders-cluster"));
        _clock.Advance(TimeSpan.FromSeconds(11));
        Code(NdpErrorCodes.ClusterSplit, () => _registry.Resolve("orders-cluster"));
        _authority[_b.Nid] = _b.Authority(Now, 2);
        Accepted(_registry.Admit(_b.Announce(Now, sequence: 2, epoch: 2)));
        Assert.Equal(_b.Nid, _registry.Resolve("orders-cluster").Nid);
    }

    [Theory]
    [InlineData("pin")][InlineData("key")][InlineData("endpoint")][InlineData("policy")][InlineData("expiry")]
    public void ChangedCurrentAuthorityRequiresNewSignedAdmission(string field)
    {
        Accepted(_registry.Admit(_a.Announce(Now)));
        var saved = _registry.Resolve("orders-cluster");
        var authority = _authority[_a.Nid];
        _authority[_a.Nid] = field switch
        {
            "pin" => authority with { TlsPin = new string('B', 64) },
            "key" => authority with { PublicKey = _b.PublicKey },
            "endpoint" => authority with { Endpoint = new("https://localhost:8444/gateway") },
            "policy" => authority with { PolicyVersion = "policy-2" },
            "expiry" => authority with { ExpiresAt = Now.AddSeconds(-1) },
            _ => throw new InvalidOperationException(),
        };
        Code(NdpErrorCodes.IssuerNotAllowed, () => _registry.ValidateCurrent(saved));
    }

    [Fact]
    public void SameIssuerAndEpochAtNewEndpointInvalidatesOldResultBinding()
    {
        Accepted(_registry.Admit(_a.Announce(Now)));
        var saved = _registry.Resolve("orders-cluster");
        var endpoint = new Uri("https://localhost:8444/gateway");
        _authority[_a.Nid] = _authority[_a.Nid] with { Endpoint = endpoint, PolicyVersion = "policy-2" };
        Accepted(_registry.Admit(_a.Announce(Now, sequence: 2, endpoint: endpoint)));
        Code(NdpErrorCodes.StateUnavailable, () => _registry.ValidateCurrent(saved));
        Assert.Equal(endpoint, _registry.Resolve("orders-cluster").Endpoint);
    }

    [Fact]
    public void ColdRestartAndRegistryIdentityCannotReuseSavedBinding()
    {
        Accepted(_registry.Admit(_a.Announce(Now)));
        var saved = _registry.Resolve("orders-cluster");
        var restarted = NewRegistry();
        Accepted(restarted.Admit(_a.Announce(Now)));
        Code(NdpErrorCodes.StateUnavailable, () => restarted.ValidateCurrent(saved));
    }

    [Fact]
    public void CapacityRetainsExpiredTombstonesAndRejectsNewPublisher()
    {
        var registry = NewRegistry(capacity: 1);
        Accepted(registry.Admit(_a.Announce(Now, sequence: 2, ttl: 1)));
        _clock.Advance(TimeSpan.FromSeconds(2));
        _authority[_b.Nid] = _b.Authority(Now, 2);
        Assert.Equal(NdpErrorCodes.StateUnavailable, registry.Admit(_b.Announce(Now, epoch: 2)).ErrorCode);
        Assert.Equal(NdpErrorCodes.GraphSeqRollback, registry.Admit(_a.Announce(Now)).ErrorCode);
        Accepted(registry.Admit(_a.Announce(Now, sequence: 3)));
        Assert.Equal(_a.Nid, registry.Resolve("orders-cluster").Nid);
    }

    public void Dispose() { _a.Dispose(); _b.Dispose(); }
}
