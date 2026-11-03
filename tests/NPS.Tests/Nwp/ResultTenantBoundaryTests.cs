// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    private string _recipientTenant = "fixture-tenant";
    private string ResultTenant(string subject) => subject == Bob ? _recipientTenant : "fixture-tenant";

    [Fact]
    public async Task IndependentlyAuthenticatedOtherTenantCannotUseARecipientGrantOrHeaderHint()
    {
        using var owner = PublicClient();
        var descriptor = await owner.CreateAsync("orders", Guid.NewGuid());
        var principal = new ResultPrincipal("fixture-tenant", Alice, "v1");
        _policy.IssueGrant(principal, _store.GetOwnerGrantScope(principal, descriptor.ResultRef), Bob,
            ["result.read", "result.aggregate"], ["id", "reason", "amount_minor"], descriptor.ExpiresAt);
        // Trusted service mapping, independently of claims or caller-supplied headers.
        _recipientTenant = "another-fixture-tenant";
        using var recipient = PublicClient(_recipientCertificate);
        var before = _handlers;
        var read = await Assert.ThrowsAsync<ResultHttpException>(() => recipient.ReadAsync(descriptor, 1, ["id"]));
        var aggregate = await Assert.ThrowsAsync<ResultHttpException>(() => recipient.AggregateAsync(descriptor, ["reason"], "amount_minor"));
        Assert.Equal("NWP-RESULT-NOT-FOUND", read.ErrorCode);
        Assert.Equal("NWP-RESULT-NOT-FOUND", aggregate.ErrorCode);
        Assert.Equal(404, read.HttpStatus);
        Assert.Equal(404, aggregate.HttpStatus);

        var frame = Frame("result.read", new { result_ref = descriptor.ResultRef, snapshot_id = descriptor.SnapshotId,
            schema_ref = descriptor.SchemaRef, limit = 1, fields = new[] { "id" } });
        using var raw = Client(_recipientCertificate);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "/gateway/invoke"))
        {
            Content = new StringContent(frame, Encoding.UTF8, "application/nwp-frame"),
        };
        request.Headers.Add("X-NPS-Required-Features", ResultActionProvider.FeatureSelection);
        request.Headers.Add("X-Tenant", "fixture-tenant");
        using var response = await raw.SendAsync(request);
        await Error(response, 404, "NWP-RESULT-NOT-FOUND");
        Assert.Equal(before, _handlers);
        Assert.Equal(1, _captures);
        Assert.Contains(_resourceAudit, audit => audit.Subject == Bob && audit.Tenant == "another-fixture-tenant"
            && audit.Outcome == "NWP-RESULT-NOT-FOUND");
    }
}
