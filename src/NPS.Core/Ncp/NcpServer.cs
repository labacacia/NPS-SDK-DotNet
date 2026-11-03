// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Alpha20;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>
/// NCP native-mode TCP server. Listens on a configured endpoint, validates the
/// connection preamble, reads the client's <see cref="HelloFrame"/>, and returns
/// an <see cref="NcpServerConnection"/> for the application to accept or reject (NPS-1 §4.6).
/// </summary>
public sealed class NcpServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TcpListener   _listener;
    private readonly NpsFrameCodec _codec;
    private readonly NcpServerOptions _options;

    /// <param name="endpoint">Local endpoint to listen on.</param>
    /// <param name="codec">Codec instance shared with accepted connections.</param>
    public NcpServer(IPEndPoint endpoint, NpsFrameCodec codec, NcpServerOptions? options = null)
    {
        _listener = new TcpListener(endpoint);
        _codec    = codec;
        _options  = options ?? new NcpServerOptions();
    }

    /// <param name="port">Port to listen on (all interfaces).</param>
    /// <param name="codec">Codec instance shared with accepted connections.</param>
    public NcpServer(int port, NpsFrameCodec codec, NcpServerOptions? options = null)
        : this(new IPEndPoint(IPAddress.Any, port), codec, options) { }

    /// <summary>Starts the listener so <see cref="AcceptConnectionAsync"/> can be called.</summary>
    public void Start() => _listener.Start();

    /// <summary>Stops the listener and releases the port binding.</summary>
    public void Stop() => _listener.Stop();

    /// <summary>
    /// Accepts the next inbound connection, validates the NPS preamble, reads the
    /// client's <see cref="HelloFrame"/>, and returns an <see cref="NcpServerConnection"/>.
    /// </summary>
    /// <exception cref="NcpPreambleInvalidException">Client did not send the valid preamble.</exception>
    /// <exception cref="NpsFrameException">First frame was not a <see cref="HelloFrame"/>.</exception>
    public async Task<NcpServerConnection> AcceptConnectionAsync(CancellationToken ct = default)
    {
        TcpClient tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        Stream    stream = tcp.GetStream();

        try
        {
            stream = await AuthenticateAsync(tcp, stream, ct).ConfigureAwait(false);

            using var preambleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_options.HandshakeReadTimeout > TimeSpan.Zero)
                preambleCts.CancelAfter(_options.HandshakeReadTimeout);

            // 1 — read & validate preamble within its own timeout.
            var preambleBuf = new byte[NcpPreamble.Length];
            await stream.ReadExactlyAsync(preambleBuf, preambleCts.Token).ConfigureAwait(false);

            NcpPreamble.Validate(preambleBuf);          // throws NcpPreambleInvalidException on mismatch

            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_options.HelloReadTimeout > TimeSpan.Zero)
                helloCts.CancelAfter(_options.HelloReadTimeout);
            var helloCt = helloCts.Token;

            // 2 — read and validate the Hello header before allocating payload.
            var (header, _) = await NcpNativeClient.ReadFrameHeaderAsync(stream, helloCt).ConfigureAwait(false);

            if (header.FrameType != FrameType.Hello)
                throw new NpsFrameException(
                    $"Expected HelloFrame (0x{(byte)FrameType.Hello:X2}) as first frame after preamble, " +
                    $"got 0x{(byte)header.FrameType:X2}.");

            if (header.EncodingTier != EncodingTier.Json || header.IsEncrypted || header.IsExtended)
                throw new NpsFrameException(
                    "HelloFrame must use an unencrypted Tier-1 JSON default header.");

            if (header.PayloadLength > _options.MaxHelloPayload)
                throw new NpsFrameException(
                    $"HelloFrame payload length {header.PayloadLength} exceeds configured maximum " +
                    $"{_options.MaxHelloPayload} bytes.");

            // 3 — read payload and deserialise HelloFrame
            var payload = new byte[header.PayloadLength];
            await stream.ReadExactlyAsync(payload, helloCt).ConfigureAwait(false);

            try { NcpSchemaBatchNegotiation.ValidateRawHello(payload); }
            catch (Exception ex) when (ex is FeatureFormatException or NcpHandshakeException)
            {
                var wire = _codec.Encode(new ErrorFrame { Status = NpsStatusCodes.ClientBadFrame,
                    Error = NcpErrorCodes.FeatureNegotiationInvalid, Message = "Native feature negotiation rejected." }, EncodingTier.Json);
                await stream.WriteAsync(wire, helloCt).ConfigureAwait(false);
                throw NcpSchemaBatchNegotiation.Invalid();
            }

            var hello = JsonSerializer.Deserialize<HelloFrame>(payload, JsonOpts)
                        ?? throw new NpsFrameException("HelloFrame payload deserialised to null.");

            return new NcpServerConnection(
                tcp,
                stream,
                _codec,
                hello,
                _options.HandshakeProfile);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    private async ValueTask<Stream> AuthenticateAsync(
        TcpClient tcp,
        Stream stream,
        CancellationToken ct)
    {
        if (_options.AuthenticateStreamAsync is null)
        {
            if (_options.RequireAuthenticatedStream)
                throw new NpsFrameException(
                    "NcpServerOptions.RequireAuthenticatedStream is true, but no AuthenticateStreamAsync hook is configured.");
            return stream;
        }

        var authenticated = await _options.AuthenticateStreamAsync(tcp, stream, ct).ConfigureAwait(false);
        if (authenticated is null)
            throw new NpsFrameException("NCP stream authentication hook returned null.");

        if (_options.RequireAuthenticatedStream && ReferenceEquals(authenticated, stream))
            throw new NpsFrameException(
                "NCP stream authentication hook returned the original stream while RequireAuthenticatedStream is true.");

        return authenticated;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
