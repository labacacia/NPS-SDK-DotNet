// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using NPS.Core;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

public sealed partial class NcpSchemaBatchSessionTests
{
    private static readonly FrameSchema TransferSchema = new() { Fields = [new("id", "uint64"), new("amount_minor", "int32")] };
    private static SchemaBatchSourceSnapshot SourceRows(int count) => new(TransferSchema,
        Enumerable.Range(1, count).Select(id => Row(id, id * 17 % 10000)).ToArray());
    private static long SourceSum(int count) => Enumerable.Range(1, count).Sum(id => (long)(id * 17 % 10000));
    private static NpsException SourceDenied() => new("Source access denied.", NpsStatusCodes.AuthForbidden, "NWP-AUTH-NID-SCOPE-VIOLATION");

    [Theory]
    [InlineData(100, EncodingTier.Json)]
    [InlineData(1000, EncodingTier.Json)]
    [InlineData(100000, EncodingTier.Json)]
    [InlineData(100, EncodingTier.MsgPack)]
    [InlineData(1000, EncodingTier.MsgPack)]
    [InlineData(100000, EncodingTier.MsgPack)]
    public async Task BoundedTransferMatchesSourceOracleAtEveryDeclaredScale(int count, EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = new TlsFixture();
        var captures = 0;
        var sourceChecks = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            ValueTask Authorize(CancellationToken _)
            {
                Assert.True(((SslStream)session.GetStream()).IsMutuallyAuthenticated);
                Assert.Equal(fixture.ClientFingerprint, ((SslStream)session.GetStream()).RemoteCertificate!.GetCertHashString(HashAlgorithmName.SHA256));
                sourceChecks++;
                return ValueTask.CompletedTask;
            }
            var publisher = new SchemaBatchStreamPublisher(session, Authorize);
            var receipt = await publisher.PublishAsync(_ =>
            {
                captures++;
                return ValueTask.FromResult(SourceRows(count));
            }, ct: timeout.Token);
            Assert.Equal(count, receipt.RowCount);
            Assert.Equal(0UL, receipt.Version);
            Assert.Equal(1 + (count + 511) / 512, receipt.FramesWritten);
            return receipt;
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        var consumerChecks = 0;
        using var reader = new SchemaBatchStreamReader(client, _ =>
        {
            Assert.Equal(fixture.ServerFingerprint, ((SslStream)client.GetStream()).RemoteCertificate!.GetCertHashString(HashAlgorithmName.SHA256));
            consumerChecks++;
            return ValueTask.CompletedTask;
        });
        var snapshot = await reader.ReadNextAsync(timeout.Token);
        Assert.Equal(count, snapshot.Rows.Count);
        Assert.Equal(SourceSum(count), snapshot.Rows.Sum(row => row[1].GetInt64()));
        Assert.Equal(Enumerable.Range(1, count), snapshot.Rows.Select(row => row[0].GetInt32()));
        var sent = await server;
        Assert.Equal(sent.EpochId, snapshot.EpochId);
        Assert.Equal(sent.AnchorRef, snapshot.AnchorRef);
        Assert.Equal(sent.BaselineTupleBytes, snapshot.RetainedBytes);
        Assert.Equal(1, captures);
        Assert.True(sourceChecks >= 1 + 2 * sent.FramesWritten);
        Assert.True(consumerChecks >= 2 * sent.FramesWritten);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task PublisherAndReaderCarryAnExactBaseUpdateWithoutChangingPriorSnapshot(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask);
            await publisher.PublishAsync(_ => ValueTask.FromResult(SourceRows(1000)), ct: timeout.Token);
            var receipt = await publisher.SendDiffAsync(0,
                [new("replace", "/0/1", JsonSerializer.SerializeToElement(99))], timeout.Token);
            Assert.Equal(1UL, receipt.Version);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var reader = new SchemaBatchStreamReader(client, _ => ValueTask.CompletedTask);
        var original = await reader.ReadNextAsync(timeout.Token);
        var update = await reader.ReadNextAsync(timeout.Token);
        Assert.Equal(0UL, original.Version);
        Assert.Equal(1UL, update.Version);
        Assert.Equal(original.EpochId, update.EpochId);
        Assert.Equal(17, original.Rows[0][1].GetInt32());
        Assert.Equal(99, update.Rows[0][1].GetInt32());
        Assert.Equal(SourceSum(1000) - 17 + 99, update.Rows.Sum(row => row[1].GetInt64()));
        await server;
    }

    [Theory]
    [InlineData(false, EncodingTier.Json)]
    [InlineData(false, EncodingTier.MsgPack)]
    [InlineData(true, EncodingTier.Json)]
    [InlineData(true, EncodingTier.MsgPack)]
    public async Task SourceDenialOrPreCancellationNeverCapturesOrWritesSchema(bool canceled, EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var captures = 0;
        var frames = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => throw SourceDenied(), progress: p => frames = p.FramesWritten);
            using var stop = new CancellationTokenSource();
            if (canceled) stop.Cancel();
            Task<SchemaBatchSendReceipt> Publish() => publisher.PublishAsync(_ =>
            { captures++; return ValueTask.FromResult(SourceRows(2)); }, ct: stop.Token);
            if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(Publish);
            else Assert.Equal(NpsStatusCodes.AuthForbidden, (await Assert.ThrowsAsync<NpsException>(Publish)).NpsStatusCode);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var reader = new SchemaBatchStreamReader(client, _ => ValueTask.CompletedTask);
        await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadNextAsync(timeout.Token));
        await server;
        Assert.Equal(0, captures);
        Assert.Equal(0, frames);
    }

    [Theory]
    [InlineData(false, EncodingTier.Json)]
    [InlineData(false, EncodingTier.MsgPack)]
    [InlineData(true, EncodingTier.Json)]
    [InlineData(true, EncodingTier.MsgPack)]
    public async Task RevocationOrCancellationWhileAwaitingCreditCannotPublishAnotherChunk(bool canceled, EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var fixture = new TlsFixture();
        var allowed = true;
        var waiting = new TaskCompletionSource<SchemaBatchSendProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session,
                _ => allowed ? ValueTask.CompletedTask : throw SourceDenied(), progress: progress =>
                { frames = progress.FramesWritten; if (progress.Stage == "awaiting_credit") waiting.TrySetResult(progress); });
            Task<SchemaBatchSendReceipt> Publish() => publisher.PublishAsync(_ => ValueTask.FromResult(SourceRows(2000)), ct: stop.Token);
            if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(Publish);
            else Assert.Equal(NpsStatusCodes.AuthForbidden, (await Assert.ThrowsAsync<NpsException>(Publish)).NpsStatusCode);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var receiver = client.CreateSchemaBatchReceiver();
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        var progress = await waiting.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, progress.FramesWritten);
        Assert.Equal(512, progress.RowsWritten);
        Assert.Null(receiver.Snapshot);
        if (canceled) stop.Cancel();
        else
        {
            allowed = false;
            await client.GetStream().WriteAsync(client.EncodeSchemaBatchFrame(receiver.GrantCredit(8)), timeout.Token);
        }
        await server;
        Assert.Equal(2, frames);
        await Assert.ThrowsAnyAsync<IOException>(() => Read(client.GetStream(), timeout.Token));
        Assert.Null(receiver.Snapshot);
    }

    [Theory]
    [InlineData("epoch", "NCP-STREAM-NOT-FOUND", EncodingTier.Json)]
    [InlineData("epoch", "NCP-STREAM-NOT-FOUND", EncodingTier.MsgPack)]
    [InlineData("sequence", "NCP-STREAM-SEQ-GAP", EncodingTier.Json)]
    [InlineData("sequence", "NCP-STREAM-SEQ-GAP", EncodingTier.MsgPack)]
    [InlineData("window", "NCP-STREAM-WINDOW-OVERFLOW", EncodingTier.Json)]
    [InlineData("window", "NCP-STREAM-WINDOW-OVERFLOW", EncodingTier.MsgPack)]
    [InlineData("data", "NCP-FRAME-FLAGS-INVALID", EncodingTier.Json)]
    [InlineData("data", "NCP-FRAME-FLAGS-INVALID", EncodingTier.MsgPack)]
    public async Task SpoofedReverseCreditFailsBeforeFurtherPublication(string fault, string expected, EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var frames = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask, progress: p => frames = p.FramesWritten);
            Assert.Equal(expected, (await Assert.ThrowsAsync<NpsException>(() => publisher.PublishAsync(
                _ => ValueTask.FromResult(SourceRows(2000)), ct: timeout.Token))).ProtocolErrorCode);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var receiver = client.CreateSchemaBatchReceiver();
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        var credit = receiver.GrantCredit(8);
        credit = fault switch
        {
            "epoch" => credit with { StreamId = Guid.NewGuid().ToString() },
            "sequence" => credit with { Seq = 0 },
            "window" => credit with { WindowSize = 9 },
            _ => credit with { Data = [Row(1, 17)] },
        };
        await client.GetStream().WriteAsync(client.EncodeSchemaBatchFrame(credit), timeout.Token);
        await server;
        Assert.Equal(2, frames);
        Assert.Null(receiver.Snapshot);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task ZeroCreditPausesUntilAnExplicitPositiveWindowAndThenCompletes(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var secondWait = new TaskCompletionSource<SchemaBatchSendProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask, progress: progress =>
            { if (progress.Stage == "awaiting_credit" && ++waits == 2) secondWait.TrySetResult(progress); });
            return await publisher.PublishAsync(_ => ValueTask.FromResult(SourceRows(1025)), ct: timeout.Token);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var receiver = client.CreateSchemaBatchReceiver();
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        await client.GetStream().WriteAsync(client.EncodeSchemaBatchFrame(receiver.GrantCredit(0)), timeout.Token);
        var paused = await secondWait.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, paused.FramesWritten);
        Assert.Equal(512, paused.RowsWritten);
        Assert.False(server.IsCompleted);
        await client.GetStream().WriteAsync(client.EncodeSchemaBatchFrame(receiver.GrantCredit(8)), timeout.Token);
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        Assert.Equal(1025, receiver.Snapshot!.Rows.Count);
        Assert.Equal(SourceSum(1025), receiver.Snapshot.Rows.Sum(row => row[1].GetInt64()));
        Assert.Equal(4, (await server).FramesWritten);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task ReplayedCreditCannotGrantAnotherWindow(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var frames = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask, progress: p => frames = p.FramesWritten);
            Assert.Equal(NcpErrorCodes.StreamSeqGap, (await Assert.ThrowsAsync<NpsException>(() => publisher.PublishAsync(
                _ => ValueTask.FromResult(SourceRows(10000)), ct: timeout.Token))).ProtocolErrorCode);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var receiver = client.CreateSchemaBatchReceiver();
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        var credit = client.EncodeSchemaBatchFrame(receiver.GrantCredit(8));
        await client.GetStream().WriteAsync(credit, timeout.Token);
        await client.GetStream().WriteAsync(credit, timeout.Token);
        for (var index = 0; index < 8; index++) receiver.Receive(await Read(client.GetStream(), timeout.Token));
        await server;
        Assert.Equal(10, frames); // Anchor + bootstrap + exactly one granted window.
        Assert.Null(receiver.Snapshot);
        await Assert.ThrowsAnyAsync<IOException>(() => Read(client.GetStream(), timeout.Token));
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task MissingCreditHasABoundedWaitAndCannotPublishATerminalSnapshot(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask, creditWait: TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<TimeoutException>(() => publisher.PublishAsync(_ => ValueTask.FromResult(SourceRows(1000)), ct: timeout.Token));
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var receiver = client.CreateSchemaBatchReceiver();
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        await server;
        Assert.Null(receiver.Snapshot);
        await Assert.ThrowsAnyAsync<IOException>(() => Read(client.GetStream(), timeout.Token));
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task OversizedSourceRowFailsWithoutSendingAPartialOrTruncatedChunk(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var source = new SchemaBatchSourceSnapshot(new() { Fields = [new("note", "string")] },
                [JsonSerializer.SerializeToElement(new[] { new string('x', 2000) })]);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask, new() { MaxPayloadBytes = 512 });
            Assert.Equal(NcpErrorCodes.FramePayloadTooLarge, (await Assert.ThrowsAsync<NpsException>(() => publisher.PublishAsync(
                _ => ValueTask.FromResult(source), ct: timeout.Token))).ProtocolErrorCode);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var receiver = client.CreateSchemaBatchReceiver();
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        await Assert.ThrowsAnyAsync<IOException>(() => Read(client.GetStream(), timeout.Token));
        Assert.Null(receiver.Snapshot);
        await server;
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task ConsumerRevocationBeforeReadClosesWithoutExposingRows(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            // No source publication is triggered just by a successful handshake.
            Assert.Equal(0, await session.GetStream().ReadAsync(new byte[1], timeout.Token));
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var reader = new SchemaBatchStreamReader(client, _ => throw SourceDenied());
        Assert.Equal(NpsStatusCodes.AuthForbidden, (await Assert.ThrowsAsync<NpsException>(() => reader.ReadNextAsync(timeout.Token))).NpsStatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadNextAsync(timeout.Token));
        await server;
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task PartialEofThenFreshSessionRecoversTheWholeSourceWithoutInheritingABase(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var abandonedEpoch = Guid.NewGuid().ToString();
        var server = Task.Run(async () =>
        {
            await using (var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token))
            await using (var session = await connection.AcceptAsync(Caps(), timeout.Token))
            {
                var anchor = SchemaBatchFrameCodec.ComputeAnchorId(TransferSchema);
                await session.GetStream().WriteAsync(session.EncodeSchemaBatchFrame(new AnchorFrame { AnchorId = anchor, Schema = TransferSchema, Ttl = 300 }), timeout.Token);
                await session.GetStream().WriteAsync(session.EncodeSchemaBatchFrame(new StreamFrame { StreamId = abandonedEpoch,
                    Seq = 0, IsLast = false, AnchorRef = anchor, Data = [Row(1, 17)], WindowSize = 1 }), timeout.Token);
                // Simulated process/connection loss before a final baseline, no retry receipt.
            }
            await using var replacement = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var recovered = await replacement.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(recovered, _ => ValueTask.CompletedTask);
            return await publisher.PublishAsync(_ => ValueTask.FromResult(SourceRows(1000)), ct: timeout.Token);
        });
        await using (var failed = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token))
        {
            using var reader = new SchemaBatchStreamReader(failed, _ => ValueTask.CompletedTask);
            await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadNextAsync(timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadNextAsync(timeout.Token));
        }
        await using var fresh = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var resumed = new SchemaBatchStreamReader(fresh, _ => ValueTask.CompletedTask);
        var snapshot = await resumed.ReadNextAsync(timeout.Token);
        Assert.Equal(0UL, snapshot.Version);
        Assert.NotEqual(abandonedEpoch, snapshot.EpochId);
        Assert.Equal(1000, snapshot.Rows.Count);
        Assert.Equal(SourceSum(1000), snapshot.Rows.Sum(row => row[1].GetInt64()));
        Assert.Equal((await server).EpochId, snapshot.EpochId);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task InvalidLocalDiffBaseClosesWithoutSendingAnUnprovenUpdate(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            var publisher = new SchemaBatchStreamPublisher(session, _ => ValueTask.CompletedTask);
            await publisher.PublishAsync(_ => ValueTask.FromResult(SourceRows(100)), ct: timeout.Token);
            Assert.Equal(NcpErrorCodes.StreamSeqGap, (await Assert.ThrowsAsync<NpsException>(() => publisher.SendDiffAsync(8,
                [new("replace", "/0/1", JsonSerializer.SerializeToElement(99))], timeout.Token))).ProtocolErrorCode);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        using var reader = new SchemaBatchStreamReader(client, _ => ValueTask.CompletedTask);
        var snapshot = await reader.ReadNextAsync(timeout.Token);
        Assert.Equal(0UL, snapshot.Version);
        await Assert.ThrowsAnyAsync<IOException>(() => reader.ReadNextAsync(timeout.Token));
        Assert.Equal(17, snapshot.Rows[0][1].GetInt32());
        await server;
    }
}
