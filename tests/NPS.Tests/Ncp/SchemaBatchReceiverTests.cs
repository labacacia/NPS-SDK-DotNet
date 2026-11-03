// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Buffers;
using MessagePack;
using NPS.Core;
using NPS.Core.Alpha20;
using NPS.Core.Anchoring;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

public sealed class SchemaBatchReceiverTests
{
    private static readonly FrameSchema Schema = new() { Fields = [new("id", "uint64"), new("amount_minor", "int32"), new("note", "string", Nullable: true)] };
    private static readonly AnchorFrame Anchor = new() { AnchorId = AnchorIdComputer.Compute(Schema), Schema = Schema, Ttl = 300 };
    private static readonly NpsFrameCodec Codec = NpsFrameCodec.CreateDefault();
    private static SchemaBatchReceiver Receiver(SchemaBatchLimits? limits = null, TimeProvider? time = null) =>
        new([new(SchemaBatchReceiver.FeatureId, 1)], limits, time);
    private static JsonElement Row(string json = "[1,42,null]") => JsonSerializer.Deserialize<JsonElement>(json);
    private static StreamFrame Chunk(string epoch, uint seq = 0, bool last = true, params JsonElement[] rows) =>
        new() { StreamId = epoch, Seq = seq, IsLast = last, AnchorRef = seq == 0 ? Anchor.AnchorId : null, Data = rows };
    private static DiffFrame Diff(string epoch, ulong version = 0, params JsonPatchOperation[] patches) => new()
    { AnchorRef = Anchor.AnchorId, EntityId = epoch, BaseSeq = version, Patch = patches.Length == 0 ? [new("replace", "/0/1", Row("43"))] : patches };
    private static void Send(SchemaBatchReceiver receiver, IFrame frame, EncodingTier tier = EncodingTier.MsgPack) => receiver.Receive(SchemaBatchFrameCodec.Encode(frame, tier));
    private static SchemaBatchReceiver Baseline(out string epoch, EncodingTier tier = EncodingTier.MsgPack, SchemaBatchLimits? limits = null)
    {
        var receiver = Receiver(limits);
        epoch = Guid.NewGuid().ToString();
        Send(receiver, Anchor, tier);
        Send(receiver, Chunk(epoch, rows: [Row()]), tier);
        return receiver;
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void BaselinePublishesOnlyAtFinalAndDiffUsesExactEpochSchemaVersion(EncodingTier tier)
    {
        using var receiver = Receiver();
        var epoch = Guid.NewGuid().ToString();
        Send(receiver, Anchor, tier);
        Send(receiver, Chunk(epoch, last: false, rows: [Row()]), tier);
        Assert.Null(receiver.Snapshot);
        Send(receiver, Chunk(epoch, 1, true, Row("[2,17,\"来源😀\"]")), tier);
        var before = receiver.Snapshot!;
        Assert.Equal(0UL, before.Version);
        Assert.Equal(2, before.Rows.Count);
        Send(receiver, Diff(epoch), tier);
        Assert.Equal(1UL, receiver.Snapshot!.Version);
        Assert.Equal(epoch, receiver.Snapshot.EpochId);
        Assert.Equal(43, receiver.Snapshot.Rows[0][1].GetInt32());
        Assert.Equal(42, before.Rows[0][1].GetInt32()); // Previously delivered state is immutable.
        Assert.Equal("来源😀", receiver.Snapshot.Rows[1][2].GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void DuplicateOrGappedChunkTerminatesWithoutPartialPublication(int sequence)
    {
        using var receiver = Receiver();
        var epoch = Guid.NewGuid().ToString();
        Send(receiver, Anchor);
        Send(receiver, Chunk(epoch, last: false, rows: [Row()]));
        var ex = Assert.Throws<NpsException>(() => Send(receiver, Chunk(epoch, (uint)sequence, true, Row())));
        Assert.Equal(NcpErrorCodes.StreamSeqGap, ex.ProtocolErrorCode);
        Assert.True(receiver.IsTerminal);
        Assert.Throws<InvalidOperationException>(() => receiver.Snapshot);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("schema")]
    [InlineData("base")]
    [InlineData("format")]
    [InlineData("duplicate")]
    public void WrongBindingOrDuplicateUpdateFailsClosed(string fault)
    {
        using var receiver = Baseline(out var epoch);
        var update = Diff(epoch);
        update = fault switch
        {
            "epoch" => update with { EntityId = Guid.NewGuid().ToString() },
            "schema" => update with { AnchorRef = "sha256:" + new string('b', 64) },
            "base" => update with { BaseSeq = 8 },
            "format" => update with { PatchFormat = NcpPatchFormat.BinaryBitset, Patch = null, BinaryBitset = [1] },
            _ => update,
        };
        if (fault == "duplicate") Send(receiver, update);
        var ex = Assert.Throws<NpsException>(() => Send(receiver, update));
        Assert.Equal(fault switch { "epoch" => NcpErrorCodes.StreamNotFound, "schema" => NcpErrorCodes.AnchorStale,
            "format" => NcpErrorCodes.DiffFormatUnsupported, _ => NcpErrorCodes.StreamSeqGap }, ex.ProtocolErrorCode);
        Assert.True(receiver.IsTerminal);
    }

    [Theory]
    [InlineData("/00/1", "replace", "43")]
    [InlineData("/-1/1", "replace", "43")]
    [InlineData("/0/3", "replace", "43")]
    [InlineData("/1/1", "replace", "43")]
    [InlineData("/0/1", "add", "43")]
    [InlineData("/0/1", "replace", "\"wrong-type\"")]
    public void InvalidSecondPatchCannotPartlyMutatePublishedState(string path, string op, string value)
    {
        using var receiver = Baseline(out var epoch);
        var before = receiver.Snapshot!;
        var update = Diff(epoch, 0, new("replace", "/0/2", Row("\"changed\"")), new(op, path, Row(value)));
        Assert.Throws<NpsException>(() => Send(receiver, update));
        Assert.True(receiver.IsTerminal);
        Assert.Equal(JsonValueKind.Null, before.Rows[0][2].ValueKind);
        Assert.Equal(42, before.Rows[0][1].GetInt32());
    }

    [Fact]
    public void CreditCannotBeEnlargedBySenderAndExplicitConsumerCreditResumes()
    {
        using var receiver = Receiver(new() { InitialCredit = 1 });
        var epoch = Guid.NewGuid().ToString();
        Send(receiver, Anchor);
        Send(receiver, Chunk(epoch, last: false, rows: [Row()]) with { WindowSize = 8 });
        var control = receiver.GrantCredit(0);
        Assert.Empty(control.Data);
        Assert.False(control.IsLast);
        Assert.Equal(0U, control.WindowSize);
        control = receiver.GrantCredit(1);
        Assert.Equal(epoch, control.StreamId);
        Send(receiver, Chunk(epoch, 1, true));
        Assert.Single(receiver.Snapshot!.Rows);

        using var exhausted = Receiver(new() { InitialCredit = 1 });
        Send(exhausted, Anchor);
        Send(exhausted, Chunk(epoch, last: false, rows: [Row()]) with { WindowSize = 8 });
        var ex = Assert.Throws<NpsException>(() => Send(exhausted, Chunk(epoch, 1, true)));
        Assert.Equal(NcpErrorCodes.StreamWindowOverflow, ex.ProtocolErrorCode);
        Assert.True(exhausted.IsTerminal);
    }

    [Theory]
    [InlineData("[1,42]")]
    [InlineData("[1,42,null,0]")]
    [InlineData("{\"id\":1,\"amount_minor\":42,\"note\":null}")]
    [InlineData("[9007199254740992,42,null]")]
    [InlineData("[1,42.5,null]")]
    [InlineData("[1,2147483648,null]")]
    [InlineData("[null,42,null]")]
    public void InvalidRowsRejectInsteadOfCoercing(string json)
    {
        using var receiver = Receiver();
        Send(receiver, Anchor);
        var ex = Assert.Throws<NpsException>(() => Send(receiver, Chunk(Guid.NewGuid().ToString(), rows: [Row(json)])));
        Assert.Equal(json.Contains("42.5") ? NcpErrorCodes.FrameFlagsInvalid : NcpErrorCodes.AnchorSchemaInvalid, ex.ProtocolErrorCode);
    }

    [Fact]
    public void UnaryCapsHasNoDeltaBaseAndCountMismatchRejects()
    {
        using var receiver = Receiver();
        Send(receiver, Anchor);
        Send(receiver, new CapsFrame { AnchorRef = Anchor.AnchorId, Count = 1, Data = [Row()] });
        Assert.Null(receiver.Snapshot!.EpochId);
        Assert.Null(receiver.Snapshot.Version);
        Assert.Throws<NpsException>(() => Send(receiver, Diff(Guid.NewGuid().ToString())));
        using var mismatch = Receiver();
        Send(mismatch, Anchor);
        Assert.Throws<NpsException>(() => Send(mismatch, new CapsFrame { AnchorRef = Anchor.AnchorId, Count = 2, Data = [Row()] }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteOrMonotonicExpiryInvalidatesPublishedState(bool rollWallBack)
    {
        var clock = new Clock();
        using var receiver = Receiver(time: clock);
        Send(receiver, Anchor);
        Send(receiver, Chunk(Guid.NewGuid().ToString(), rows: [Row()]));
        clock.Elapsed = TimeSpan.FromSeconds(300);
        clock.WallOffset = rollWallBack ? TimeSpan.FromSeconds(-1) : clock.Elapsed;
        var ex = Assert.Throws<NpsException>(() => receiver.Snapshot);
        Assert.Equal(NcpErrorCodes.AnchorStale, ex.ProtocolErrorCode);
        Assert.True(receiver.IsTerminal);
    }

    [Fact]
    public void MissingPoisonedOrReorderedSchemaCannotEstablishBaseline()
    {
        using var missing = Receiver();
        Assert.Equal(NcpErrorCodes.AnchorNotFound, Assert.Throws<NpsException>(() => Send(missing,
            Chunk(Guid.NewGuid().ToString(), rows: [Row()]))).ProtocolErrorCode);
        using var poisoned = Receiver();
        var reordered = Schema with { Fields = Schema.Fields.Reverse().ToArray() };
        Assert.NotEqual(Anchor.AnchorId, AnchorIdComputer.Compute(reordered));
        Assert.Equal(NcpErrorCodes.AnchorIdMismatch, Assert.Throws<NpsException>(() => Send(poisoned,
            Anchor with { Schema = reordered })).ProtocolErrorCode);
    }

    [Fact]
    public void FaultThenFreshFullBaselineRecoversTheSourceOracle()
    {
        using var failed = Baseline(out var oldEpoch);
        Assert.Throws<NpsException>(() => Send(failed, Diff(oldEpoch, 2)));
        using var fresh = Receiver();
        var newEpoch = Guid.NewGuid().ToString();
        Assert.NotEqual(oldEpoch, newEpoch);
        Send(fresh, Anchor);
        var source = new[] { Row("[1,43,null]"), Row("[2,17,\"new\"]") };
        Send(fresh, Chunk(newEpoch, rows: source));
        Assert.Equal(0UL, fresh.Snapshot!.Version);
        Assert.Equal(source.Select(r => r.GetRawText()), fresh.Snapshot.Rows.Select(r => r.GetRawText()));
        Assert.Equal(60, fresh.Snapshot.Rows.Sum(r => r[1].GetInt32()));
    }

    [Fact]
    public void LimitsIncludeArrayPunctuationAndCancellationRetainsNoLiveState()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { Row() }).Length;
        using var exact = Baseline(out _, limits: new() { MaxRetainedBytes = bytes });
        Assert.Equal(bytes, exact.Snapshot!.RetainedBytes);
        using var over = Receiver(new() { MaxRetainedBytes = bytes - 1 });
        Send(over, Anchor);
        Assert.Equal(NcpErrorCodes.StreamLimitExceeded, Assert.Throws<NpsException>(() => Send(over,
            Chunk(Guid.NewGuid().ToString(), rows: [Row()]))).ProtocolErrorCode);
        using var canceled = Receiver();
        Send(canceled, Anchor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => canceled.Receive(Codec.Encode(Chunk(Guid.NewGuid().ToString())), cts.Token));
        Assert.True(canceled.IsTerminal);
    }

    [Fact]
    public void RetainedRowsUseTheSameCompactRepresentationAsTheirByteBudget()
    {
        var compact = JsonSerializer.SerializeToUtf8Bytes(new[] { Row() });
        using var receiver = Receiver(new() { MaxRetainedBytes = compact.Length });
        Send(receiver, Anchor, EncodingTier.Json);
        var encoded = Codec.Encode(Chunk(Guid.NewGuid().ToString(), rows: [Row()]), EncodingTier.Json);
        var header = NpsFrameCodec.PeekHeader(encoded);
        var json = System.Text.Encoding.UTF8.GetString(encoded.AsSpan(header.HeaderSize));
        // Send raw whitespace that a conforming encoder would already have compacted.
        json = json.Replace("\"data\":[[1,42,null]]", "\"data\":[[1," + new string(' ', 12000) + "42,null]]");
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var wire = new byte[header.HeaderSize + payload.Length];
        (header with { PayloadLength = (uint)payload.Length }).WriteTo(wire);
        payload.CopyTo(wire, header.HeaderSize);
        receiver.Receive(wire);
        var snapshot = receiver.Snapshot!;
        Assert.Equal(compact.Length, snapshot.RetainedBytes);
        Assert.Equal("[1,42,null]", snapshot.Rows[0].GetRawText());
        Assert.Equal(snapshot.RetainedBytes,
            System.Text.Encoding.UTF8.GetByteCount("[" + snapshot.Rows[0].GetRawText() + "]"));
    }

    [Fact]
    public void UnselectedOrWrongRevisionNeverConstructsAReceiver()
    {
        Assert.Throws<NpsException>(() => new SchemaBatchReceiver([]));
        Assert.Throws<NpsException>(() => new SchemaBatchReceiver([new(SchemaBatchReceiver.FeatureId, 2)]));
        Assert.Throws<NpsException>(() => new SchemaBatchReceiver([new(SchemaBatchReceiver.FeatureId, 1), new(SchemaBatchReceiver.FeatureId, 1)]));
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void FloatWireTokenCannotBeNormalizedIntoAnInteger(EncodingTier tier)
    {
        using var receiver = Receiver();
        Send(receiver, Anchor, tier);
        var ex = Assert.Throws<NpsException>(() => Send(receiver,
            Chunk(Guid.NewGuid().ToString(), rows: [Row("[1,42.0,null]")]), tier));
        Assert.Equal(NcpErrorCodes.FrameFlagsInvalid, ex.ProtocolErrorCode);
        Assert.True(receiver.IsTerminal);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    [InlineData("final")]
    [InlineData("payload")]
    [InlineData("null-data")]
    public void InvalidWireCannotBypassTypedStateChecks(string fault)
    {
        using var receiver = Receiver(new() { MaxPayloadBytes = 2048 });
        Send(receiver, Anchor, EncodingTier.Json);
        var wire = Codec.Encode(Chunk(Guid.NewGuid().ToString(), rows: [Row()]), EncodingTier.Json);
        if (fault == "final") wire[1] ^= 0x04;
        else if (fault == "trailing") wire = [.. wire, 0];
        else if (fault == "payload")
            wire = Codec.Encode(Chunk(Guid.NewGuid().ToString(), rows: [Row(JsonSerializer.Serialize(new object?[] { 1, 42, new string('x', 3000) }))]), EncodingTier.Json);
        else
        {
            var header = NpsFrameCodec.PeekHeader(wire);
            var json = System.Text.Encoding.UTF8.GetString(wire.AsSpan(header.HeaderSize));
            json = fault == "duplicate" ? json.Replace("\"seq\":0", "\"seq\":99,\"seq\":0")
                : json.Replace("\"data\":[[1,42,null]]", "\"data\":null");
            var payload = System.Text.Encoding.UTF8.GetBytes(json);
            wire = new byte[FrameHeader.DefaultSize + payload.Length];
            (header with { PayloadLength = (uint)payload.Length }).WriteTo(wire);
            payload.CopyTo(wire, FrameHeader.DefaultSize);
        }
        var ex = Assert.Throws<NpsException>(() => receiver.Receive(wire));
        Assert.Equal(fault == "payload" ? NcpErrorCodes.FramePayloadTooLarge
            : fault == "null-data" ? NcpErrorCodes.StreamLimitExceeded : NcpErrorCodes.FrameFlagsInvalid, ex.ProtocolErrorCode);
        Assert.True(receiver.IsTerminal);
    }

    [Fact]
    public void SenderCannotRefreshCreditOrSwitchSchemaInsideAnEpoch()
    {
        using var receiver = Receiver();
        var epoch = Guid.NewGuid().ToString();
        Send(receiver, Anchor);
        Send(receiver, Chunk(epoch, last: false));
        Assert.Equal(NcpErrorCodes.FrameFlagsInvalid, Assert.Throws<NpsException>(() => Send(receiver,
            Chunk(epoch, 1, true) with { WindowSize = 8 })).ProtocolErrorCode);
        using var switched = Receiver();
        Send(switched, Anchor);
        Send(switched, Chunk(epoch, last: false));
        Assert.Equal(NcpErrorCodes.AnchorStale, Assert.Throws<NpsException>(() => Send(switched,
            Chunk(epoch, 1, true) with { AnchorRef = "sha256:" + new string('f', 64) })).ProtocolErrorCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("binary")]
    [InlineData("extension")]
    public void MessagePackDuplicatesAndNonStringCellsCannotBeNormalizedIntoValidRows(string fault)
    {
        using var receiver = Receiver();
        Send(receiver, Anchor);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(fault == "duplicate" ? 6 : 5);
        writer.Write("stream_id"); writer.Write(Guid.NewGuid().ToString());
        writer.Write("seq"); writer.Write(0);
        if (fault == "duplicate") { writer.Write("seq"); writer.Write(0); }
        writer.Write("is_last"); writer.Write(true);
        writer.Write("anchor_ref"); writer.Write(Anchor.AnchorId);
        writer.Write("data"); writer.WriteArrayHeader(1); writer.WriteArrayHeader(3);
        writer.Write(1); writer.Write(42);
        if (fault == "binary") writer.Write(new byte[] { 65 });
        else if (fault == "extension") writer.Write(DateTime.UnixEpoch);
        else writer.WriteNil();
        writer.Flush();
        var wire = new byte[FrameHeader.DefaultSize + buffer.WrittenCount];
        new FrameHeader(FrameType.Stream, (FrameFlags)((byte)EncodingTier.MsgPack | 0x04),
            (uint)buffer.WrittenCount).WriteTo(wire);
        buffer.WrittenSpan.CopyTo(wire.AsSpan(FrameHeader.DefaultSize));
        Assert.Equal(NcpErrorCodes.FrameFlagsInvalid,
            Assert.Throws<NpsException>(() => receiver.Receive(wire)).ProtocolErrorCode);
        Assert.True(receiver.IsTerminal);
    }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + WallOffset;
    }
}
