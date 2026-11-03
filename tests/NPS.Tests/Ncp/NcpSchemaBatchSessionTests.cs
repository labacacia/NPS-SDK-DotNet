// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MessagePack;
using NPS.Core;
using NPS.Core.Anchoring;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Core.Serialization;

namespace NPS.Tests.Ncp;

// Real loopback/pinned mutual TLS and public native APIs. Provisioned pins are not
// general PKI/NIP/source-policy qualification or a NWP live-result-stream claim.
public sealed partial class NcpSchemaBatchSessionTests
{
    private static readonly NpsFrameCodec Codec = NpsFrameCodec.CreateDefault();
    private static HelloFrame Hello(EncodingTier tier = EncodingTier.MsgPack, bool features = true) => new()
    {
        NpsVersion = "0.11", SupportedProtocols = ["ncp"],
        SupportedEncodings = [tier == EncodingTier.Json ? "json" : "msgpack"],
        FeatureNegotiation = features ? 1U : null,
        FeatureOffer = features ? JsonSerializer.Deserialize<JsonElement>(
            "[{\"id\":\"nps.schema_batches\",\"revisions\":[{\"revision\":1,\"lifecycle\":\"experimental\"}]}]") : null,
        RequiredFeatures = features ? [SchemaBatchReceiver.FeatureId] : null,
    };
    private static NcpHandshakeCapsFrame Caps() => new() { NodeId = "urn:nps:node:fixture:batch-source", Caps = ["ncp"] };
    private static JsonElement Row(int id, int amount) => JsonSerializer.SerializeToElement(new[] { id, amount });

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task ActualMutualTlsSessionSelectsThenTransfersBaselineCreditAndBoundDiff(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = new TlsFixture();
        var schema = new FrameSchema { Fields = [new("id", "uint64"), new("amount_minor", "int32")] };
        var anchor = new AnchorFrame { AnchorId = AnchorIdComputer.Compute(schema), Schema = schema, Ttl = 300 };
        var epoch = Guid.NewGuid().ToString();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            Assert.Single(session.SelectedFeatures);
            Assert.True(((SslStream)session.GetStream()).IsMutuallyAuthenticated);
            await session.GetStream().WriteAsync(session.EncodeSchemaBatchFrame(anchor), timeout.Token);
            await session.GetStream().WriteAsync(session.EncodeSchemaBatchFrame(new StreamFrame { StreamId = epoch, Seq = 0, IsLast = false,
                AnchorRef = anchor.AnchorId, Data = [Row(1, 42)], WindowSize = 1 }), timeout.Token);
            // Actual reverse credit; a sender waits instead of queueing the entire source.
            var control = Assert.IsType<StreamFrame>(SchemaBatchFrameCodec.Decode(await Read(session.GetStream(), timeout.Token)));
            Assert.Equal(epoch, control.StreamId);
            Assert.Empty(control.Data);
            Assert.False(control.IsLast);
            Assert.Equal(1U, control.WindowSize);
            await session.GetStream().WriteAsync(session.EncodeSchemaBatchFrame(new StreamFrame { StreamId = epoch, Seq = 1, IsLast = true,
                Data = [Row(2, 17)] }), timeout.Token);
            await session.GetStream().WriteAsync(session.EncodeSchemaBatchFrame(new DiffFrame { AnchorRef = anchor.AnchorId, EntityId = epoch, BaseSeq = 0,
                Patch = [new("replace", "/0/1", JsonSerializer.SerializeToElement(43))] }), timeout.Token);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        Assert.Equal(new[] { new NPS.Core.Alpha20.FeatureSelection(SchemaBatchReceiver.FeatureId, 1) }, client.SelectedFeatures);
        using var receiver = client.CreateSchemaBatchReceiver(new() { InitialCredit = 1 });
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        Assert.Null(receiver.Snapshot);
        await client.GetStream().WriteAsync(client.EncodeSchemaBatchFrame(receiver.GrantCredit(1)), timeout.Token);
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        Assert.Equal(0UL, receiver.Snapshot!.Version);
        receiver.Receive(await Read(client.GetStream(), timeout.Token));
        Assert.Equal(1UL, receiver.Snapshot!.Version);
        Assert.Equal(60, receiver.Snapshot.Rows.Sum(row => row[1].GetInt32()));
        Assert.Equal(2, receiver.Snapshot.Rows.Count);
        await server;
    }

    [Fact]
    public async Task RequiredDisabledFeatureRejectsBeforeApplicationDispatch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture(enabled: false);
        var dispatched = 0;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            var error = await Assert.ThrowsAsync<NcpHandshakeException>(async () =>
            {
                await using var admitted = await connection.AcceptAsync(Caps(), timeout.Token);
                Interlocked.Increment(ref dispatched);
            });
            Assert.Equal(NcpErrorCodes.FeatureRequiredUnsupported, error.ErrorCode);
            // The application only dispatches after a successful AcceptAsync.
            Assert.Equal(0, dispatched);
        });
        var exception = await Assert.ThrowsAsync<NcpHandshakeException>(() => fixture.Client().ConnectAsync(
            "127.0.0.1", fixture.Port, Hello(), timeout.Token));
        Assert.Equal(NcpErrorCodes.FeatureRequiredUnsupported, exception.ErrorCode);
        Assert.Equal(NpsStatusCodes.ClientConflict, exception.NpsStatusCode);
        await server;
    }

    [Fact]
    public async Task ReconnectDoesNotInheritFeatureSelectionAndFactoryEnforcesNegotiatedTier()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
                await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
                Assert.Equal(i == 0 ? 1 : 0, session.SelectedFeatures.Count);
            }
        });
        await using (var first = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(), timeout.Token))
        {
            using var receiver = first.CreateSchemaBatchReceiver();
            var schema = new FrameSchema { Fields = [new("id", "uint64")] };
            var wrongTier = Codec.Encode(new AnchorFrame { AnchorId = AnchorIdComputer.Compute(schema), Schema = schema, Ttl = 300 }, EncodingTier.Json);
            Assert.Throws<NpsEncodingUnsupportedException>(() => receiver.Receive(wrongTier));
        }
        await using var second = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(features: false), timeout.Token);
        Assert.Empty(second.SelectedFeatures);
        Assert.Throws<NpsException>(() => second.CreateSchemaBatchReceiver());
        Assert.Throws<NpsException>(() => second.EncodeSchemaBatchFrame(new StreamFrame
            { StreamId = Guid.NewGuid().ToString(), Seq = 0, IsLast = true, Data = [] }));
        await second.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => second.CreateSchemaBatchReceiver());
        await server;
    }

    [Fact]
    public async Task SessionEncodingAndReceiverBothApplyTheNegotiatedPayloadCeiling()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var applicationBytes = -1;
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps(), timeout.Token);
            applicationBytes = await session.GetStream().ReadAsync(new byte[1], timeout.Token);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port,
            Hello() with { MaxFramePayload = 512 }, timeout.Token);
        var frame = new StreamFrame { StreamId = Guid.NewGuid().ToString(), Seq = 0, IsLast = true,
            Data = [JsonSerializer.SerializeToElement(new object[] { 1, 42, new string('x', 1024) })] };
        Assert.Equal(NcpErrorCodes.FramePayloadTooLarge, Assert.Throws<NpsException>(
            () => client.EncodeSchemaBatchFrame(frame)).ProtocolErrorCode);
        using var receiver = client.CreateSchemaBatchReceiver();
        Assert.Equal(NcpErrorCodes.FramePayloadTooLarge, Assert.Throws<NpsException>(
            () => receiver.Receive(SchemaBatchFrameCodec.Encode(frame, EncodingTier.MsgPack))).ProtocolErrorCode);
        await client.DisposeAsync();
        await server;
        Assert.Equal(0, applicationBytes);
    }

    [Fact]
    public async Task LocalExperimentalOptInIsRequiredBeforeConnecting()
    {
        await using var fixture = new TlsFixture();
        var exception = await Assert.ThrowsAsync<NcpHandshakeException>(() => fixture.Client(enabled: false)
            .ConnectAsync("127.0.0.1", fixture.Port, Hello()));
        Assert.Equal(NcpErrorCodes.FeatureRequiredUnsupported, exception.ErrorCode);
        Assert.Equal(0, fixture.ClientAuthentications);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task IndependentHandshakeMetadataMayContainFloatingValues(EncodingTier tier)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Task.Run(async () =>
        {
            await using var connection = await fixture.Server.AcceptConnectionAsync(timeout.Token);
            await using var session = await connection.AcceptAsync(Caps() with
                { Payload = JsonSerializer.SerializeToElement(new { fixture_load = 1.25 }) }, timeout.Token);
        });
        await using var client = await fixture.Client().ConnectAsync("127.0.0.1", fixture.Port, Hello(tier), timeout.Token);
        Assert.Equal(1.25, client.ServerCaps.Payload!.Value.GetProperty("fixture_load").GetDouble());
        Assert.Single(client.SelectedFeatures);
        using var receiver = client.CreateSchemaBatchReceiver();
        Assert.Throws<InvalidOperationException>(() => client.CreateSchemaBatchReceiver());
        await client.DisposeAsync();
        Assert.True(receiver.IsTerminal);
        await server;
    }

    [Fact]
    public async Task ExplicitFeatureOptInCannotAdmitAPlaintextReferenceBinding()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        fixture.Server.Stop();
        await using var plain = new NcpServer(new IPEndPoint(IPAddress.Loopback, fixture.Port), Codec,
            new() { HandshakeProfile = new() { SchemaBatchesEnabled = true } });
        plain.Start();
        var server = Task.Run(async () =>
        {
            await using var connection = await plain.AcceptConnectionAsync(timeout.Token);
            Assert.Equal(NcpErrorCodes.FeatureRequiredUnsupported,
                (await Assert.ThrowsAsync<NcpHandshakeException>(() => connection.AcceptAsync(Caps(), timeout.Token))).ErrorCode);
        });
        var client = new NcpNativeClient(Codec, new() { SchemaBatchesEnabled = true });
        Assert.Equal(NcpErrorCodes.FeatureRequiredUnsupported, (await Assert.ThrowsAsync<NcpHandshakeException>(
            () => client.ConnectAsync("127.0.0.1", fixture.Port, Hello(), timeout.Token))).ErrorCode);
        await server;
    }

    [Theory]
    [InlineData("old")]
    [InlineData("unoffered")]
    [InlineData("duplicate")]
    [InlineData("revision")]
    [InlineData("float-revision")]
    [InlineData("oversized")]
    public async Task OldOrMaliciousPeerSelectionClosesBeforeApplicationBytes(string fault)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        fixture.Server.Stop();
        var listener = new TcpListener(IPAddress.Loopback, fixture.Port);
        listener.Start();
        try
        {
            var applicationBytes = -1;
            var peer = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = await fixture.AuthenticateServer(tcp, tcp.GetStream(), timeout.Token);
                var preamble = new byte[NcpPreamble.Length];
                await stream.ReadExactlyAsync(preamble, timeout.Token);
                Assert.True(NcpPreamble.Matches(preamble));
                Assert.IsType<HelloFrame>(Codec.Decode(await Read(stream, timeout.Token)));
                var one = "{\"id\":\"nps.schema_batches\",\"revision\":1}";
                var selected = fault switch
                {
                    "unoffered" => "[{\"id\":\"example.not_offered\",\"revision\":1}]",
                    "duplicate" => "[" + one + "," + one + "]",
                    "revision" => "[{\"id\":\"nps.schema_batches\",\"revision\":2}]",
                    "float-revision" => "[{\"id\":\"nps.schema_batches\",\"revision\":1.0}]",
                    _ => "[" + one + "]",
                };
                var response = Caps() with { FeatureNegotiation = fault == "old" ? null : 1,
                    SelectedFeatures = fault == "old" ? null : JsonSerializer.Deserialize<JsonElement>(selected),
                    Payload = fault == "oversized" ? JsonSerializer.SerializeToElement(new string('x', 17000)) : null };
                if (fault == "oversized")
                {
                    var wire = CanonicalPeerCaps(response);
                    await stream.WriteAsync(wire.AsMemory(0, NpsFrameCodec.PeekHeader(wire).HeaderSize), timeout.Token);
                }
                else await stream.WriteAsync(fault == "old" ? Codec.Encode(response, EncodingTier.MsgPack) : CanonicalPeerCaps(response), timeout.Token);
                applicationBytes = await stream.ReadAsync(new byte[1], timeout.Token);
            });
            var error = await Assert.ThrowsAsync<NcpHandshakeException>(() => fixture.Client().ConnectAsync(
                "127.0.0.1", fixture.Port, Hello(), timeout.Token));
            Assert.Equal(fault == "old" ? NcpErrorCodes.FeatureRequiredUnsupported : NcpErrorCodes.FeatureNegotiationInvalid, error.ErrorCode);
            Assert.Equal(fault == "old" ? NpsStatusCodes.ClientConflict : NpsStatusCodes.ClientBadFrame, error.NpsStatusCode);
            await peer;
            Assert.Equal(0, applicationBytes);
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("size")]
    public async Task ActualRawHelloRejectsAmbiguityAndBoundsBeforeAccept(string fault)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fixture = new TlsFixture();
        var server = Assert.ThrowsAsync<NcpHandshakeException>(() => fixture.Server.AcceptConnectionAsync(timeout.Token));
        // Establish independent TLS identity, then send intentionally malformed raw Hello.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, fixture.Port, timeout.Token);
        await using var stream = await fixture.AuthenticateClient(tcp, tcp.GetStream(), timeout.Token);
        await NcpPreamble.WriteAsync(stream, timeout.Token);
        var json = Encoding.UTF8.GetString(Codec.Encode(Hello(), EncodingTier.Json).AsSpan(FrameHeader.DefaultSize));
        if (fault == "duplicate") json = json.Replace("\"feature_negotiation\":1", "\"feature_negotiation\":0,\"feature_negotiation\":1");
        else if (fault == "version") json = json.Replace("\"feature_negotiation\":1", "\"feature_negotiation\":2");
        else json = json[..^1] + ",\"padding\":\"" + new string('x', 17000) + "\"}";
        var payload = Encoding.UTF8.GetBytes(json);
        var wire = new byte[FrameHeader.DefaultSize + payload.Length];
        new FrameHeader(FrameType.Hello, FrameFlags.Final, (uint)payload.Length).WriteTo(wire);
        payload.CopyTo(wire, FrameHeader.DefaultSize);
        await stream.WriteAsync(wire, timeout.Token);
        var response = Assert.IsType<ErrorFrame>(Codec.Decode(await Read(stream, timeout.Token)));
        Assert.Equal(NcpErrorCodes.FeatureNegotiationInvalid, response.Error);
        Assert.Equal(NcpErrorCodes.FeatureNegotiationInvalid, (await server).ErrorCode);
    }

    private static byte[] CanonicalPeerCaps(NcpHandshakeCapsFrame caps)
    {
        var json = Codec.Encode(caps, EncodingTier.Json);
        var root = JsonElement.Parse(json.AsSpan(NpsFrameCodec.PeekHeader(json).HeaderSize));
        var options = MessagePackSerializerOptions.Standard.WithResolver(NpsMessagePackResolver.Instance);
        var payload = MessagePackSerializer.Serialize(root, options);
        var wire = new byte[FrameHeader.DefaultSize + payload.Length];
        new FrameHeader(FrameType.Caps, FrameFlags.Tier2MsgPack | FrameFlags.Final, (uint)payload.Length).WriteTo(wire);
        payload.CopyTo(wire, FrameHeader.DefaultSize);
        return wire;
    }

    private static async Task<byte[]> Read(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, ct);
        var headerSize = (prefix[1] & 0x80) == 0 ? FrameHeader.DefaultSize : FrameHeader.ExtendedSize;
        var raw = new byte[headerSize];
        prefix.CopyTo(raw, 0);
        await stream.ReadExactlyAsync(raw.AsMemory(2), ct);
        var header = NpsFrameCodec.PeekHeader(raw);
        Assert.True(header.PayloadLength <= 65536);
        var wire = new byte[raw.Length + header.PayloadLength];
        raw.CopyTo(wire, 0);
        await stream.ReadExactlyAsync(wire.AsMemory(raw.Length), ct);
        return wire;
    }

    private sealed class TlsFixture : IAsyncDisposable
    {
        private readonly X509Certificate2 _serverCertificate = Certificate(server: true);
        private readonly X509Certificate2 _clientCertificate = Certificate(server: false);
        private readonly bool _noDelay;
        public NcpServer Server { get; }
        public int Port { get; }
        public int ClientAuthentications { get; private set; }
        public string ClientFingerprint => _clientCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        public string ServerFingerprint => _serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        public X509Certificate2 ServerCertificate => _serverCertificate;
        public X509Certificate2 ClientCertificate => _clientCertificate;
        public TlsFixture(bool enabled = true, NpsFrameCodec? codec = null, bool noDelay = false)
        {
            _noDelay = noDelay;
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); Port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Server = new NcpServer(new IPEndPoint(IPAddress.Loopback, Port), codec ?? Codec, new()
            {
                RequireAuthenticatedStream = true,
                AuthenticateStreamAsync = AuthenticateServer,
                HandshakeProfile = new() { SchemaBatchesEnabled = enabled },
            });
            Server.Start();
        }
        public NcpNativeClient Client(bool enabled = true) => new(Codec, new()
        { SchemaBatchesEnabled = enabled, RequireAuthenticatedStream = true, AuthenticateStreamAsync = AuthenticateClient });
        public async ValueTask<Stream> AuthenticateClient(TcpClient tcp, Stream transport, CancellationToken ct)
        {
            if (_noDelay) tcp.NoDelay = true;
            ClientAuthentications++;
            var ssl = new SslStream(transport, false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost", ClientCertificates = new() { _clientCertificate },
                    AllowTlsResume = false,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Pinned(certificate, _serverCertificate),
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct);
                return ssl;
            }
            catch { await ssl.DisposeAsync(); throw; }
        }
        public async ValueTask<Stream> AuthenticateServer(TcpClient tcp, Stream transport, CancellationToken ct)
        {
            if (_noDelay) tcp.NoDelay = true;
            var ssl = new SslStream(transport, false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCertificate, ClientCertificateRequired = true,
                    AllowTlsResume = false,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Pinned(certificate, _clientCertificate),
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, ct);
                return ssl;
            }
            catch { await ssl.DisposeAsync(); throw; }
        }
        private static bool Pinned(X509Certificate? actual, X509Certificate2 expected) => actual is not null
            && actual.GetCertHashString(HashAlgorithmName.SHA256) == expected.GetCertHashString(HashAlgorithmName.SHA256)
            && DateTime.UtcNow >= expected.NotBefore.ToUniversalTime() && DateTime.UtcNow < expected.NotAfter.ToUniversalTime();
        private static X509Certificate2 Certificate(bool server)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(server ? "CN=localhost" : "CN=batch-consumer", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
                { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }
        public async ValueTask DisposeAsync()
        { await Server.DisposeAsync(); _serverCertificate.Dispose(); _clientCertificate.Dispose(); }
    }
}
