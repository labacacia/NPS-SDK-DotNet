// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using MessagePack;
using NPS.Core.Anchoring;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;
using NPS.Core.Ncp;
using NPS.Core.Serialization;

namespace NPS.Tests.Ncp;

public sealed class SchemaBatchRepresentationTests
{
    [Fact]
    public void ExplicitProfileUsesCanonicalMessagePackKeysWithoutChangingTheLegacyCodec()
    {
        var frame = new StreamFrame { StreamId = Guid.NewGuid().ToString(), Seq = 0, IsLast = true,
            AnchorRef = "sha256:" + new string('a', 64), Data = [JsonSerializer.SerializeToElement(new[] { 1, 42 })] };
        var json = SchemaBatchFrameCodec.Encode(frame, EncodingTier.Json);
        var binary = SchemaBatchFrameCodec.Encode(frame, EncodingTier.MsgPack);
        var options = MessagePackSerializerOptions.Standard.WithResolver(NpsMessagePackResolver.Instance);
        var root = MessagePackSerializer.Deserialize<JsonElement>(binary.AsMemory(NpsFrameCodec.PeekHeader(binary).HeaderSize), options);
        Assert.True(root.TryGetProperty("stream_id", out _));
        Assert.False(root.TryGetProperty("StreamId", out _));
        Assert.True(JsonElement.DeepEquals(JsonElement.Parse(json.AsSpan(NpsFrameCodec.PeekHeader(json).HeaderSize)), root));
        var legacy = NpsFrameCodec.CreateDefault().Encode(frame, EncodingTier.MsgPack);
        var old = MessagePackSerializer.Deserialize<JsonElement>(legacy.AsMemory(NpsFrameCodec.PeekHeader(legacy).HeaderSize), options);
        Assert.True(old.TryGetProperty("StreamId", out _));
        Assert.False(old.TryGetProperty("stream_id", out _));
        Assert.Throws<NPS.Core.Exceptions.NpsException>(() => SchemaBatchFrameCodec.Decode(legacy));
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void ExistingCodecKeepsObjectKeysAndDoesNotChoosePositionalRows(EncodingTier tier)
    {
        var schema = new FrameSchema { Fields = [new("order_identifier", "uint64"), new("refund_amount_minor", "int32")] };
        var anchor = AnchorIdComputer.Compute(schema);
        var rows = JsonSerializer.SerializeToElement(new[]
        {
            new { order_identifier = 1, refund_amount_minor = 42 },
            new { order_identifier = 2, refund_amount_minor = 17 },
        });
        var frame = new CapsFrame { AnchorRef = anchor, Count = 2, Data = rows.EnumerateArray().ToArray() };
        var registry = FrameRegistry.CreateDefault();
        var codec = new NpsFrameCodec(new Tier1JsonCodec(), new Tier2MsgPackCodec(), registry);
        var wire = codec.Encode(frame, tier);
        var decoded = Assert.IsType<CapsFrame>(codec.Decode(wire));
        Assert.All(decoded.Data, row => Assert.Equal(JsonValueKind.Object, row.ValueKind));
        Assert.Equal(42, decoded.Data[0].GetProperty("refund_amount_minor").GetInt32());
        var payload = wire.AsSpan(wire.Length - checked((int)NpsFrameCodec.PeekHeader(wire).PayloadLength)).ToArray();
        var inspection = tier == EncodingTier.Json ? Encoding.UTF8.GetString(payload)
            : MessagePackSerializer.ConvertToJson(payload);
        Assert.Equal(2, inspection.Split("order_identifier").Length - 1);
        Assert.Equal(2, inspection.Split("refund_amount_minor").Length - 1);
    }
}
