// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    private Action<ResultOperationAudit>? _beforeResultReply;

    [Fact]
    public async Task PublicClientCancellationBeforeAdmissionHasZeroResultDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = PublicClient();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateAsync("orders", Guid.NewGuid(), ct: cancellation.Token));
        Assert.Equal(0, _captures);
        Assert.Equal(0, _handlers);
        Assert.Equal(0, _posts);
        Assert.Empty(_mintedProofs);
    }

    [Fact]
    public async Task PublicClientInFlightCancellationDoesNotUndoAllocationAndSameKeyRecovers()
    {
        var allocated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeResultReply = audit =>
        {
            if (audit.Operation != "result.create" || audit.Outcome != "success") return;
            // Deliberately stall only AFTER store publication, before the reply.
            allocated.TrySetResult();
            if (!releaseReply.Task.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Fixture reply barrier expired.");
        };
        using var cancellation = new CancellationTokenSource();
        using var client = PublicClient();
        var key = Guid.NewGuid();
        try
        {
            var pending = client.CreateAsync("orders", key, ct: cancellation.Token);
            await allocated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, _captures);
            Assert.Contains(_resourceAudit, audit => audit.Operation == "result.create" && audit.Outcome == "success");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            _beforeResultReply = null;
            releaseReply.TrySetResult();
        }
        // Explicit retry: same logical key, independently discovered/authenticated
        // client, fresh signed projection. No automatic retry or rollback assertion.
        using var recovery = PublicClient();
        var descriptor = await recovery.CreateAsync("orders", key);
        Assert.Equal(2, descriptor.RowCount);
        Assert.Equal(1, _captures);
        Assert.Single((await recovery.ReadAsync(descriptor, 1, ["id"])).Rows);
        Assert.True((await recovery.ReleaseAsync(descriptor)).Released);
    }
}
