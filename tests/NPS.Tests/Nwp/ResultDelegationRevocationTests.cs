// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    [Fact]
    public async Task RecipientAggregateGrantCannotExposeAnotherFieldOrSurviveRevocation()
    {
        using var owner = PublicClient();
        var descriptor = await owner.CreateAsync("orders", Guid.NewGuid());
        var principal = new ResultPrincipal("fixture-tenant", Alice, "v1");
        var grant = _policy.IssueGrant(principal, _store.GetOwnerGrantScope(principal, descriptor.ResultRef),
            Bob, ["result.aggregate"], ["reason"], descriptor.ExpiresAt);
        using var recipient = PublicClient(_recipientCertificate);
        var before = _handlers;
        var hidden = await Assert.ThrowsAsync<ResultHttpException>(() => recipient.AggregateAsync(descriptor, ["reason"], "amount_minor"));
        Assert.Equal(404, hidden.HttpStatus);
        Assert.Equal("NWP-RESULT-NOT-FOUND", hidden.ErrorCode);
        Assert.Equal(before, _handlers);
        var visible = await recipient.AggregateAsync(descriptor, ["reason"]);
        Assert.Equal(2, Assert.Single(visible.Groups).GetProperty("count").GetInt32());
        Assert.False(visible.Groups[0].TryGetProperty("sum", out _));
        Assert.True(_policy.RevokeGrant(principal, grant.GrantId));
        before = _handlers;
        var revoked = await Assert.ThrowsAsync<ResultHttpException>(() => recipient.AggregateAsync(descriptor, ["reason"]));
        Assert.Equal(404, revoked.HttpStatus);
        Assert.Equal("NWP-RESULT-NOT-FOUND", revoked.ErrorCode);
        Assert.Equal(before, _handlers);
        Assert.Equal(1, _captures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredSourceResultsRecheckSourceAndOwnerRevocationForReadsAndAggregates(bool revokeOwner)
    {
        _oracleScale = 100;
        _policy.SetSourceAuthority(Authority() with { Fields = ["id", "period", "reason", "refunded", "amount_minor"] });
        using var owner = PublicClient();
        var all = await owner.CreateAsync("orders", Guid.NewGuid());
        var query = System.Text.Json.JsonSerializer.SerializeToElement(new { filter = new { refunded = true } });
        var filtered = await owner.CreateAsync("orders", Guid.NewGuid(), query);
        Assert.NotEqual(all.SnapshotId, filtered.SnapshotId);
        var principal = new ResultPrincipal("fixture-tenant", Alice, "v1");
        foreach (var descriptor in new[] { all, filtered })
            _policy.IssueGrant(principal, _store.GetOwnerGrantScope(principal, descriptor.ResultRef), Bob,
                ["result.read", "result.aggregate"], ["id", "period", "reason", "amount_minor"], descriptor.ExpiresAt);
        using var recipient = PublicClient(_recipientCertificate);
        Assert.Single((await recipient.ReadAsync(filtered, 1, ["id"])).Rows);
        Assert.NotEmpty((await recipient.AggregateAsync(filtered, ["reason", "period"], "amount_minor")).Groups);
        if (revokeOwner) await _identityCaStore.RevokeAsync(Alice, "fixture owner revocation", DateTime.UtcNow);
        else _policy.SetSourceAuthority(Authority() with
            { Fields = ["id", "period", "reason", "refunded", "amount_minor"], Enabled = false });
        var before = _handlers;
        foreach (var descriptor in new[] { all, filtered })
        {
            var read = await Assert.ThrowsAsync<ResultHttpException>(() => recipient.ReadAsync(descriptor, 1, ["id"]));
            var aggregate = await Assert.ThrowsAsync<ResultHttpException>(() => recipient.AggregateAsync(descriptor, ["reason", "period"], "amount_minor"));
            Assert.Equal("NWP-RESULT-NOT-FOUND", read.ErrorCode);
            Assert.Equal("NWP-RESULT-NOT-FOUND", aggregate.ErrorCode);
            Assert.Equal(404, read.HttpStatus);
            Assert.Equal(404, aggregate.HttpStatus);
        }
        Assert.Equal(before, _handlers);
        Assert.Equal(2, _captures);
    }
}
