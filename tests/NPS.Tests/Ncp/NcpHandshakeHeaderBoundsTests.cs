using System.Net;
using System.Net.Sockets;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

public sealed class NcpHandshakeHeaderBoundsTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [FrameType.Caps, (byte)0x80, uint.MaxValue, 65535u, 65535u, true, new[] { "json" }, NcpErrorCodes.FramePayloadTooLarge];
        yield return [FrameType.Caps, (byte)0, 16385u, 16384u, 65535u, false, new[] { "json" }, NcpErrorCodes.FramePayloadTooLarge];
        yield return [FrameType.Caps, (byte)0, 1025u, 65535u, 1024u, false, new[] { "json" }, NcpErrorCodes.FramePayloadTooLarge];
        yield return [FrameType.Caps, (byte)0x80, 8u, 65535u, 65535u, false, new[] { "json" }, NcpErrorCodes.FrameFlagsInvalid];
        yield return [FrameType.Caps, (byte)0x08, 8u, 65535u, 65535u, false, new[] { "json" }, NcpErrorCodes.EncNotNegotiated];
        yield return [FrameType.Caps, (byte)2, 8u, 65535u, 65535u, false, new[] { "json", "binary_vector.v1" }, NcpErrorCodes.EncodingUnsupported];
        yield return [FrameType.Caps, (byte)1, 8u, 65535u, 65535u, false, new[] { "json" }, NcpErrorCodes.EncodingUnsupported];
        yield return [FrameType.Stream, (byte)0, 8u, 65535u, 65535u, false, new[] { "json" }, "NCP-HANDSHAKE-UNEXPECTED-FRAME"];
        yield return [FrameType.Error, (byte)0x80, uint.MaxValue, uint.MaxValue, 65535u, true, new[] { "json" }, NcpErrorCodes.FramePayloadTooLarge];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HeaderOnlyPeerIsRejectedBeforeBodyReadOrApplicationWrite(FrameType type, byte flags,
        uint payload, uint helloLimit, uint codecLimit, bool ext, string[] encodings, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = accepted.GetStream();
            var preamble = new byte[8]; await stream.ReadExactlyAsync(preamble, deadline.Token);
            Assert.True(NcpPreamble.Matches(preamble));
            var helloHeader = new byte[4]; await stream.ReadExactlyAsync(helloHeader, deadline.Token);
            var hello = FrameHeader.Parse(helloHeader);
            Assert.Equal(FrameType.Hello, hello.FrameType);
            var body = new byte[hello.PayloadLength]; await stream.ReadExactlyAsync(body, deadline.Token);
            var header = new FrameHeader(type, (FrameFlags)flags, payload);
            var wire = new byte[header.IsExtended ? 8 : 4]; header.WriteTo(wire);
            await stream.WriteAsync(wire, deadline.Token);
            // Deliberately no response body. Client must reject the header, close,
            // and send zero application bytes; waiting for a body fails this test.
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        }, deadline.Token);
        var client = new NcpNativeClient(NpsFrameCodec.CreateDefault(codecLimit));
        var error = await Assert.ThrowsAnyAsync<NpsException>(() => client.ConnectAsync("127.0.0.1", port,
            new HelloFrame { NpsVersion = "0.12", MinVersion = "0.1", SupportedEncodings = encodings,
                SupportedProtocols = ["ncp"], MaxFramePayload = helloLimit, ExtSupport = ext }, deadline.Token));
        Assert.Equal(expected, error.ProtocolErrorCode);
        await peer;
    }
}
