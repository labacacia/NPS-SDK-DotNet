// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Core.Serialization;
using System.Text;

namespace NPS.Tests.Ncp;

public sealed class SchemaBatchSchemaHashTests
{
    public static IEnumerable<object[]> Cases()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "spec/conformance/alpha20/schema_batch_hash_vectors.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(10, document.RootElement.GetProperty("cases").GetArrayLength());
            foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
                foreach (var tier in new[] { EncodingTier.Json, EncodingTier.MsgPack })
                    yield return [item.GetProperty("id").GetString()!, item.GetRawText(), tier];
            yield break;
        }
        throw new FileNotFoundException("Shared schema JCS supplement is required.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IndependentUnicodeSchemaHashPublishesThroughEncodedFrames(string id, string raw, EncodingTier tier)
    {
        Assert.StartsWith("SB-H", id);
        using var document = JsonDocument.Parse(raw);
        var item = document.RootElement;
        var schema = new FrameSchema { Fields = item.GetProperty("schema").GetProperty("fields").EnumerateArray()
            .Select(field => new SchemaField(field.GetProperty("name").GetString()!, field.GetProperty("type").GetString()!,
                field.TryGetProperty("semantic", out var semantic) ? semantic.GetString() : null, field.GetProperty("nullable").GetBoolean())).ToArray() };
        var hash = item.GetProperty("anchor_id").GetString()!;
        Assert.Equal(hash, SchemaBatchFrameCodec.ComputeAnchorId(schema));
        using var receiver = new SchemaBatchReceiver([new(SchemaBatchReceiver.FeatureId, 1)]);
        receiver.Receive(SchemaBatchFrameCodec.Encode(new AnchorFrame { AnchorId = hash, Schema = schema, Ttl = 300 }, tier));
        receiver.Receive(SchemaBatchFrameCodec.Encode(new StreamFrame { StreamId = Guid.NewGuid().ToString(), Seq = 0,
            IsLast = true, AnchorRef = hash, Data = [JsonSerializer.SerializeToElement(new[] { 1 })] }, tier));
        Assert.Equal(hash, receiver.Snapshot!.AnchorRef);
        Assert.Equal(1, receiver.Snapshot.Rows[0][0].GetInt32());
    }

    [Fact]
    public void InvalidUnicodeRejectsBeforeHashingOrAppendingAndNormalizationIsNeverApplied()
    {
        var output = new StringBuilder("prefix");
        Assert.Throws<EncoderFallbackException>(() => JcsString.Append(output, "\ud800"));
        Assert.Equal("prefix", output.ToString());
        Assert.Throws<EncoderFallbackException>(() => SchemaBatchFrameCodec.ComputeAnchorId(new()
            { Fields = [new("\udfff", "uint64")] }));
        Assert.NotEqual(SchemaBatchFrameCodec.ComputeAnchorId(new() { Fields = [new("é", "uint64")] }),
            SchemaBatchFrameCodec.ComputeAnchorId(new() { Fields = [new("e\u0301", "uint64")] }));
    }
}
