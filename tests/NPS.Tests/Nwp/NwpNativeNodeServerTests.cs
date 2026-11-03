// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Core.Registry;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.Registry;
using NPS.NWP.MemoryNode;
using NPS.NWP.Native;

namespace NPS.Tests.Nwp;

public sealed partial class NwpNativeNodeServerTests
{
    [Fact]
    public async Task DispatchQuery_UsesMemoryProvider()
    {
        var server = new NwpNativeNodeServer(
            MakeCodec(),
            new NwpNativeNodeOptions
            {
                MemorySchema = new MemoryNodeSchema
                {
                    TableName = "products",
                    PrimaryKey = "id",
                    Fields = [new MemoryNodeField { Name = "id", Type = "int" }],
                },
                MemoryAnchorRef = "sha256:test",
                MemoryOptions = new MemoryNodeOptions
                {
                    NodeId = "urn:nps:node:test:memory",
                    PathPrefix = "/memory",
                    Schema = new MemoryNodeSchema
                    {
                        TableName = "products",
                        PrimaryKey = "id",
                        Fields = [new MemoryNodeField { Name = "id", Type = "int" }],
                    },
                },
            },
            memoryProvider: new StubMemoryProvider());

        var response = Assert.IsType<CapsFrame>(await server.DispatchAsync(new QueryFrame { Limit = 1, RequestId = "req-query-1" }));

        Assert.Equal("sha256:test", response.AnchorRef);
        Assert.Equal(1u, response.Count);
        Assert.Equal(1, response.Data[0].GetProperty("id").GetInt32());
        Assert.Equal("req-query-1", response.RequestId);
    }

    [Fact]
    public async Task DispatchAction_UsesActionProvider()
    {
        var server = new NwpNativeNodeServer(
            MakeCodec(),
            new NwpNativeNodeOptions
            {
                ActionOptions = new ActionNodeOptions
                {
                    NodeId = "urn:nps:node:test:actions",
                    PathPrefix = "/actions",
                    Actions = new Dictionary<string, ActionSpec>
                    {
                        ["orders.ping"] = new() { Async = false, ResultAnchor = "sha256:result" },
                    },
                },
            },
            actionProvider: new StubActionProvider());

        var response = Assert.IsType<CapsFrame>(await server.DispatchAsync(
            new ActionFrame { ActionId = "orders.ping", RequestId = "req-action-1" }));

        Assert.Equal("sha256:result", response.AnchorRef);
        Assert.Equal("ok", response.Data[0].GetProperty("status").GetString());
        Assert.Equal("req-action-1", response.RequestId);
    }

    [Fact]
    public async Task ServeAction_ReportsExactDecoderPayloadLength()
    {
        var codec = MakeCodec();
        var provider = new StubActionProvider();
        var request = codec.Encode(
            new ActionFrame
            {
                ActionId = "orders.ping",
                Params = JsonSerializer.SerializeToElement(new { value = "decoder-boundary" }),
                RequestId = "req-wire-input-1",
            },
            EncodingTier.MsgPack);
        var expected = NpsFrameCodec.PeekHeader(request).PayloadLength;
        var stream = new ScriptedDuplexStream(request);
        var server = new NwpNativeNodeServer(
            codec,
            new NwpNativeNodeOptions
            {
                ActionOptions = new ActionNodeOptions
                {
                    NodeId = "urn:nps:node:test:actions",
                    PathPrefix = "/actions",
                    Actions = new Dictionary<string, ActionSpec>
                    {
                        ["orders.ping"] = new() { Async = false, ResultAnchor = "sha256:result" },
                    },
                },
            },
            actionProvider: provider);

        await server.ServeAsync(stream, EncodingTier.MsgPack);

        Assert.IsType<CapsFrame>(codec.Decode(stream.Written));
        Assert.Equal((ulong)expected, provider.LastWireInputBytes);
    }

    [Fact]
    public async Task ServeAsync_RejectsBinaryVectorFrame_WhenPolicyDoesNotEnableIt()
    {
        var codec = MakeCodec();
        var request = codec.Encode(MakeVectorQuery(), EncodingTier.BinaryVector);
        var stream = new ScriptedDuplexStream(request);
        var server = new NwpNativeNodeServer(codec, new NwpNativeNodeOptions());

        await server.ServeAsync(stream, EncodingTier.MsgPack);

        var response = Assert.IsType<ErrorFrame>(codec.Decode(stream.Written));
        Assert.Equal(NpsStatusCodes.ServerEncodingUnsupported, response.Status);
        Assert.Equal(NcpErrorCodes.EncodingUnsupported, response.Error);
    }

    [Fact]
    public async Task ServeAsync_AllowsBinaryVectorQuery_WhenPolicyEnablesIt()
    {
        var codec = MakeCodec();
        var request = codec.Encode(MakeVectorQuery(), EncodingTier.BinaryVector);
        var stream = new ScriptedDuplexStream(request);
        var server = new NwpNativeNodeServer(
            codec,
            new NwpNativeNodeOptions
            {
                MemorySchema = new MemoryNodeSchema
                {
                    TableName = "products",
                    PrimaryKey = "id",
                    Fields = [new MemoryNodeField { Name = "id", Type = "int" }],
                },
                MemoryOptions = new MemoryNodeOptions
                {
                    NodeId = "urn:nps:node:test:memory",
                    PathPrefix = "/memory",
                    Schema = new MemoryNodeSchema
                    {
                        TableName = "products",
                        PrimaryKey = "id",
                        Fields = [new MemoryNodeField { Name = "id", Type = "int" }],
                    },
                },
            },
            memoryProvider: new StubMemoryProvider());

        await server.ServeAsync(stream, new NcpEncodingPolicy(EncodingTier.MsgPack, BinaryVectorEnabled: true));

        var response = Assert.IsType<CapsFrame>(codec.Decode(stream.Written));
        Assert.Equal(1u, response.Count);
    }

    [Fact]
    public async Task ServeAsync_BinaryVectorMalformedFixtures_ReturnDocumentedClientErrors()
    {
        var codec = MakeCodec();
        var server = new NwpNativeNodeServer(codec, new NwpNativeNodeOptions());
        var fixture = LoadBinaryVectorFixture();

        foreach (var testCase in fixture.Vectors.Where(item => item.Kind == "negative"))
        {
            var payload = Convert.FromHexString(testCase.Input.PayloadHex);
            var header = new FrameHeader(
                (FrameType)testCase.Input.FrameType,
                FrameFlags.Tier3BinaryVector | FrameFlags.Final,
                (uint)payload.Length);
            var headerBytes = new byte[FrameHeader.DefaultSize];
            header.WriteTo(headerBytes);

            var stream = new ScriptedDuplexStream(headerBytes.Concat(payload).ToArray());

            await server.ServeAsync(stream, new NcpEncodingPolicy(EncodingTier.MsgPack, BinaryVectorEnabled: true));

            var response = Assert.IsType<ErrorFrame>(codec.Decode(stream.Written));
            Assert.Equal(testCase.Expected.Status, response.Status);
            Assert.Equal(testCase.Expected.Error, response.Error);
        }
    }

    [Fact]
    public async Task ServeAsync_ReservedTier_ReturnsFrameFlagsInvalid()
    {
        var codec = MakeCodec();
        var request = new byte[] { (byte)FrameType.Query, 0x07, 0x00, 0x00 };
        var stream = new ScriptedDuplexStream(request);
        var server = new NwpNativeNodeServer(codec, new NwpNativeNodeOptions());

        await server.ServeAsync(stream, new NcpEncodingPolicy(EncodingTier.MsgPack, BinaryVectorEnabled: true));

        var response = Assert.IsType<ErrorFrame>(codec.Decode(stream.Written));
        Assert.Equal(NpsStatusCodes.ClientBadFrame, response.Status);
        Assert.Equal(NcpErrorCodes.FrameFlagsInvalid, response.Error);
    }

    private static QueryFrame MakeVectorQuery() => new()
    {
        Limit = 1,
        VectorSearch = new VectorSearchOptions
        {
            Field = "embedding",
            Vector = [0.25f, -1.5f, 3.0f],
            TopK = 1,
        },
    };

    private static FrameRegistry BuildNwpRegistry()
        => new FrameRegistryBuilder()
            .AddNcp()
            .AddNwp()
            .Build();

    private static NpsFrameCodec MakeCodec() =>
        new(new Tier1JsonCodec(), new Tier2MsgPackCodec(), BuildNwpRegistry());

    private static BinaryVectorFixture LoadBinaryVectorFixture()
    {
        var path = FindRepoFile("spec/conformance/ncp/binary_vector_payload_vectors.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BinaryVectorFixture>(json)!;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Unable to locate {relativePath} from {AppContext.BaseDirectory}.");
    }

    private sealed record BinaryVectorFixture(
        [property: JsonPropertyName("vectors")] IReadOnlyList<BinaryVectorCase> Vectors);

    private sealed record BinaryVectorCase(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("input")] BinaryVectorInput Input,
        [property: JsonPropertyName("expected")] BinaryVectorExpected Expected);

    private sealed record BinaryVectorInput(
        [property: JsonPropertyName("frame_type")] int FrameType,
        [property: JsonPropertyName("payload_hex")] string PayloadHex);

    private sealed record BinaryVectorExpected(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("error")] string Error);

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _output = new();

        public ScriptedDuplexStream(byte[] input) => _input = new MemoryStream(input);

        public byte[] Written => _output.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);
    }

    private sealed class StubMemoryProvider : IMemoryNodeProvider
    {
        public Task<MemoryNodeQueryResult> QueryAsync(
            QueryFrame frame,
            MemoryNodeSchema schema,
            MemoryNodeOptions options,
            CancellationToken ct = default) =>
            Task.FromResult(new MemoryNodeQueryResult
            {
                Rows = [new Dictionary<string, object?> { ["id"] = 1 }],
            });

        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
            QueryFrame frame,
            MemoryNodeSchema schema,
            MemoryNodeOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return [new Dictionary<string, object?> { ["id"] = 1 }];
            await Task.CompletedTask;
        }

        public Task<long> CountAsync(QueryFrame frame, MemoryNodeSchema schema, CancellationToken ct = default) =>
            Task.FromResult(1L);
    }

    private sealed class StubActionProvider : IActionNodeProvider
    {
        public ulong LastWireInputBytes { get; private set; }

        public Task<ActionExecutionResult> ExecuteAsync(
            ActionFrame frame,
            ActionContext context,
            CancellationToken ct = default)
        {
            LastWireInputBytes = context.WireInputBytes;
            using var doc = JsonDocument.Parse("""{"status":"ok"}""");
            return Task.FromResult(new ActionExecutionResult
            {
                Result = doc.RootElement.Clone(),
                TokenEst = 1,
            });
        }
    }
}
