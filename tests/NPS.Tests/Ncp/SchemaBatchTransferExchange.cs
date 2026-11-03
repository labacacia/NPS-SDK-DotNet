// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Ncp;
using NPS.Core.Registry;
using NPS.NWP.Frames;
using NPS.NWP.Registry;

namespace NPS.Tests.Ncp;

public sealed partial class NcpSchemaBatchSessionTests
{
    private static readonly NpsFrameCodec TransferCodec = NpsFrameCodec.Create(new FrameRegistryBuilder().AddNcp().AddNwp().Build());

    private sealed class TransferExchange : IAsyncDisposable
    {
        private readonly TransferSource _source;
        private readonly string _arm;
        private readonly TlsFixture _tls = new(codec: TransferCodec, noDelay: true);
        private readonly CancellationTokenSource _stop = new();
        private WebApplication? _http;
        private Task? _native;
        private Uri? _endpoint;
        public int GzipResponses;
        public TransferExchange(TransferSource source, string arm) { _source = source; _arm = arm; }
        public bool Native => _arm.StartsWith("ncp-", StringComparison.Ordinal);
        public EncodingTier Tier => _arm.EndsWith("msgpack", StringComparison.Ordinal) ? EncodingTier.MsgPack : EncodingTier.Json;

        public async Task StartAsync()
        {
            if (Native) { _native = ServeNativeAsync(); return; }
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseSockets(options => options.NoDelay = true);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = _tls.ServerCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => certificate.GetCertHashString(HashAlgorithmName.SHA256) == _tls.ClientFingerprint;
                https.OnAuthenticate = (_, options) => options.AllowTlsResume = false;
            })));
            _http = builder.Build();
            _http.MapPost("/snapshot", ServeHttpAsync);
            await _http.StartAsync();
            _endpoint = new Uri(_http.Urls.Single());
        }

        private static ActionFrame Command(int variant) => new()
        {
            ActionId = "benchmark.snapshot", Params = JsonSerializer.SerializeToElement(new { variant }), TimeoutMs = 120000,
        };
        private static int Variant(ActionFrame command)
        {
            Assert.Equal("benchmark.snapshot", command.ActionId);
            Assert.False(command.Async);
            Assert.Null(command.IdempotencyKey); // Pure fixture read; no retry/cache semantics are inferred.
            var variant = command.Params!.Value.GetProperty("variant").GetInt32();
            Assert.InRange(variant, 0, 1);
            return variant;
        }
        private ValueTask Authorize(NcpSession session, bool source)
        {
            var ssl = Assert.IsType<SslStream>(session.GetStream());
            Assert.True(ssl.IsAuthenticated && ssl.IsMutuallyAuthenticated);
            Assert.Equal(source ? _tls.ClientFingerprint : _tls.ServerFingerprint,
                ssl.RemoteCertificate!.GetCertHashString(HashAlgorithmName.SHA256));
            return ValueTask.CompletedTask;
        }

        private async Task ServeNativeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await using var connection = await _tls.Server.AcceptConnectionAsync(_stop.Token);
                    await using var session = await connection.AcceptAsync(Caps() with { Caps = ["ncp", "nwp"] }, _stop.Token);
                    while (!_stop.IsCancellationRequested)
                    {
                        byte[] request;
                        try { request = await Read(session.GetStream(), _stop.Token); }
                        catch (EndOfStreamException) { break; }
                        Assert.InRange(request.Length, 4, 16384);
                        await Authorize(session, source: true);
                        var variant = Variant(Assert.IsType<ActionFrame>(TransferCodec.Decode(request)));
                        var publisher = new SchemaBatchStreamPublisher(session, _ => Authorize(session, source: true));
                        await publisher.PublishAsync(_ => ValueTask.FromResult(_source.CaptureTuples(variant)), ct: _stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeHttpAsync(HttpContext context)
        {
            Assert.True(context.Request.IsHttps);
            Assert.Equal(_tls.ClientFingerprint, context.Connection.ClientCertificate!.GetCertHashString(HashAlgorithmName.SHA256));
            Assert.InRange(context.Request.ContentLength ?? -1, 1, 16384);
            using var request = new MemoryStream();
            await context.Request.Body.CopyToAsync(request, context.RequestAborted);
            Assert.InRange(request.Length, 1, 16384);
            var command = JsonSerializer.Deserialize<ActionFrame>(request.GetBuffer().AsSpan(0, checked((int)request.Length)))!;
            var variant = Variant(command);
            var msgpack = _arm.Contains("msgpack", StringComparison.Ordinal);
            context.Response.ContentType = msgpack ? "application/msgpack" : "application/json";
            if (_arm.EndsWith("gzip", StringComparison.Ordinal))
            {
                context.Response.Headers.ContentEncoding = "gzip";
                Interlocked.Increment(ref GzipResponses);
                await using var gzip = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
                await EncodeBodyAsync(gzip, _source.Bodies[variant], msgpack, context.RequestAborted);
            }
            else await EncodeBodyAsync(context.Response.Body, _source.Bodies[variant], msgpack, context.RequestAborted);
        }

        // Standard streaming APIs include encoding/compression/HTTP chunk framing
        // without retaining duplicate full encoded/compressed response arrays.
        private static Task EncodeBodyAsync(Stream output, object body, bool msgpack, CancellationToken ct) => msgpack
            ? MessagePackSerializer.SerializeAsync(output, body, MessagePackSerializerOptions.Standard, ct)
            : JsonSerializer.SerializeAsync(output, body, body.GetType(), cancellationToken: ct);

        public async Task<TransferFrontend> OpenAsync(TransferTraffic traffic, CancellationToken ct)
        {
            if (Native)
            {
                var client = new NcpNativeClient(TransferCodec, new()
                {
                    SchemaBatchesEnabled = true, RequireAuthenticatedStream = true,
                    AuthenticateStreamAsync = (tcp, stream, token) => _tls.AuthenticateClient(tcp, traffic.Wrap(stream), token),
                });
                var session = await client.ConnectAsync("127.0.0.1", _tls.Port,
                    Hello(Tier) with { SupportedProtocols = ["ncp", "nwp"] }, ct);
                return new(this, session);
            }
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = _arm.EndsWith("gzip", StringComparison.Ordinal) ? DecompressionMethods.GZip : DecompressionMethods.None,
                SslOptions = new()
                {
                    TargetHost = "localhost", AllowTlsResume = false, ClientCertificates = new() { _tls.ClientCertificate },
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.GetCertHashString(HashAlgorithmName.SHA256) == _tls.ServerFingerprint,
                },
            };
            traffic.Configure(handler);
            var http = new HttpClient(handler)
            {
                BaseAddress = _endpoint, Timeout = TimeSpan.FromSeconds(120),
                DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            return new(this, http);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_http is not null) await _http.DisposeAsync();
            await _tls.DisposeAsync();
            if (_native is not null) await _native;
            _stop.Dispose();
        }

        public sealed class TransferFrontend : IAsyncDisposable
        {
            private readonly TransferExchange _exchange;
            private readonly NcpSession? _session;
            private readonly HttpClient? _http;
            public TransferFrontend(TransferExchange exchange, NcpSession session) { _exchange = exchange; _session = session; }
            public TransferFrontend(TransferExchange exchange, HttpClient http) { _exchange = exchange; _http = http; }
            public async Task<TransferredSnapshot> FetchAsync(int variant, CancellationToken ct)
            {
                if (_session is not null)
                {
                    await _exchange.Authorize(_session, source: false);
                    await _session.GetStream().WriteAsync(TransferCodec.Encode(Command(variant), _exchange.Tier), ct);
                    using var reader = new SchemaBatchStreamReader(_session, _ => _exchange.Authorize(_session, source: false));
                    var snapshot = await reader.ReadNextAsync(ct);
                    return new(snapshot.AnchorRef, snapshot.Rows, true, snapshot.EpochId, snapshot.RetainedBytes);
                }
                using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Command(variant)));
                content.Headers.ContentType = new("application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/snapshot") { Content = content };
                using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var body = _exchange._arm.Contains("msgpack", StringComparison.Ordinal)
                    ? await MessagePackSerializer.DeserializeAsync<NamedTransferBody>(input, MessagePackSerializerOptions.Standard, ct)
                    : (await JsonSerializer.DeserializeAsync<NamedTransferBody>(input, cancellationToken: ct))!;
                var schema = new FrameSchema { Fields = body.Schema.Fields
                    .Select(field => new SchemaField(field.Name, field.Type, Nullable: field.Nullable)).ToArray() };
                return new(SchemaBatchFrameCodec.ComputeAnchorId(schema), [], false, null, null, body.Rows);
            }
            public async ValueTask DisposeAsync()
            {
                if (_session is not null) await _session.DisposeAsync();
                _http?.Dispose();
            }
        }
    }
}
