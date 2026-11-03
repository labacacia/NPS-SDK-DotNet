// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Net.Security;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;

namespace NPS.Core.Ncp;

/// <summary>
/// NCP native-mode TCP client. Performs the 3-step handshake
/// (preamble → HelloFrame → NcpHandshakeCapsFrame) per NPS-1 §4.6
/// and returns a live <see cref="NcpSession"/>.
/// </summary>
public sealed class NcpNativeClient
{
    private static readonly FrameRegistry HandshakeRegistry =
        new FrameRegistryBuilder()
            .AddNcpHandshake()
            .Build();

    private static readonly Tier1JsonCodec JsonCodec = new();
    private static readonly Tier2MsgPackCodec MsgPackCodec = new();
    private static readonly Tier3BinaryVectorCodec BinaryVectorCodec = new();

    private readonly NpsFrameCodec _codec;
    private readonly NcpNativeClientOptions _options;

    /// <param name="codec">Codec instance used to encode the outbound <see cref="HelloFrame"/>.</param>
    /// <param name="options">Host authentication hook and explicit local experimental policy; defaults enable no feature.</param>
    public NcpNativeClient(NpsFrameCodec codec, NcpNativeClientOptions? options = null)
    { _codec = codec; _options = options ?? new(); }

    /// <summary>
    /// Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/>, performs
    /// the NCP native-mode handshake, and returns a live session.
    /// </summary>
    /// <exception cref="NcpHandshakeException">Server rejected the handshake or sent an unexpected frame.</exception>
    public async Task<NcpSession> ConnectAsync(
        string host,
        int port,
        HelloFrame hello,
        CancellationToken ct = default)
    {
        NcpSchemaBatchNegotiation.ValidateClientOffer(hello, _options.SchemaBatchesEnabled);
        var helloWire = _codec.Encode(hello, EncodingTier.Json);
        var helloHeader = NpsFrameCodec.PeekHeader(helloWire);
        NcpSchemaBatchNegotiation.ValidateRawHello(helloWire.AsSpan(helloHeader.HeaderSize));
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        Stream stream = tcp.GetStream();
        try
        {
            if (_options.AuthenticateStreamAsync is { } authenticate)
            {
                var authenticated = await authenticate(tcp, stream, ct).ConfigureAwait(false);
                if (authenticated is null || _options.RequireAuthenticatedStream && ReferenceEquals(authenticated, stream))
                    throw new NpsFrameException("Native client authentication hook did not verify a wrapped transport.");
                stream = authenticated;
            }
            else if (_options.RequireAuthenticatedStream)
                throw new NpsFrameException("Native client requires a host transport authentication hook.");
            // 1 — preamble (Tier-1 JSON encoding not yet negotiated)
            await NcpPreamble.WriteAsync(stream, ct).ConfigureAwait(false);

            // 2 — HelloFrame (always JSON per spec — encoding not yet agreed)
            await stream.WriteAsync(helloWire, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // 3 — read server response header (handles EXT flag)
            var (header, _) = await ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false);

            // Validate the untrusted header BEFORE allocation or waiting for its body.
            // This path uses a handshake-specific registry, but must still respect
            // the same local codec ceiling as normal frame decoding.
            ValidateResponseHeader(header, hello);

            // 4 — read payload
            var payload = new byte[header.PayloadLength];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            if (hello.FeatureNegotiation is not null && header.FrameType == FrameType.Caps)
            {
                try { SchemaBatchWireGuard.Validate(payload, header.EncodingTier, header.FrameType, strictValues: false); }
                catch (NpsException) { throw NcpSchemaBatchNegotiation.Invalid(); }
            }

            // 5 — ErrorFrame → throw
            if (header.FrameType == FrameType.Error)
            {
                var err = (ErrorFrame)DecodeHandshakeFrame(header, payload);
                throw new NcpHandshakeException(err.Error, err.Message);
            }

            // 6 — decode NcpHandshakeCapsFrame using the negotiated tier the server
            // signalled as the stable default in the response header flags.
            var negotiatedTier = header.EncodingTier;
            var caps = (NcpHandshakeCapsFrame)DecodeHandshakeFrame(header, payload, hello.FeatureNegotiation is not null);
            var policy = NcpEncodingPolicy.FromEnabledEncodings(negotiatedTier, caps.EnabledEncodings);
            var selected = NcpSchemaBatchNegotiation.ValidateResponse(hello, caps, _options.SchemaBatchesEnabled);
            if (selected.Count != 0 && stream is not SslStream { IsAuthenticated: true, IsMutuallyAuthenticated: true })
                throw new NcpHandshakeException(NcpErrorCodes.FeatureRequiredUnsupported, "Schema batch reference requires verified mutual TLS.");

            return new NcpSession(tcp, stream, caps, policy, selected);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    private void ValidateResponseHeader(FrameHeader header, HelloFrame hello)
    {
        if (hello.FeatureNegotiation is not null && header.PayloadLength > 16384)
            throw NcpSchemaBatchNegotiation.Invalid();
        if (header.PayloadLength > Math.Min(hello.MaxFramePayload, _codec.MaxPayload)
            || header.PayloadLength > int.MaxValue)
            throw new NpsFrameException("Handshake response exceeds the local payload limit.",
                NpsStatusCodes.LimitPayload, NcpErrorCodes.FramePayloadTooLarge);
        if (header.IsExtended && !hello.ExtSupport)
            throw new NpsFrameException("Extended handshake response was not offered.",
                NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
        if (header.IsEncrypted)
            throw new NpsFrameException("Handshake response encryption was not negotiated.",
                NpsStatusCodes.ClientBadFrame, NcpErrorCodes.EncNotNegotiated);
        if (header.FrameType is not (FrameType.Caps or FrameType.Error))
            throw new NcpHandshakeException("NCP-HANDSHAKE-UNEXPECTED-FRAME",
                "Expected a handshake CapsFrame or ErrorFrame.");
        if (header.EncodingTier == EncodingTier.BinaryVector)
            throw new NpsEncodingUnsupportedException("BinaryVector is not a stable handshake encoding.");
        // Pre-admission errors may always use JSON, even for a MsgPack-only offer.
        if (header.FrameType == FrameType.Caps)
        {
            var token = header.EncodingTier == EncodingTier.Json ? "json" : "msgpack";
            if (!hello.SupportedEncodings.Contains(token, StringComparer.Ordinal))
                throw new NpsEncodingUnsupportedException("Handshake encoding was not offered.");
        }
    }

    private static IFrame DecodeHandshakeFrame(FrameHeader header, ReadOnlySpan<byte> payload, bool featureResponse = false)
    {
        if (featureResponse && header.FrameType == FrameType.Caps)
        {
            var body = NcpCanonicalWire.ReadBody(payload, header.EncodingTier);
            if (header.EncodingTier == EncodingTier.MsgPack && body.ValueKind == System.Text.Json.JsonValueKind.Object
                && body.TryGetProperty("NodeId", out _))
            {
                // Recognize an old base-only peer, never a PascalCase feature selection.
                var legacy = (NcpHandshakeCapsFrame)MsgPackCodec.Decode(header.FrameType, payload, HandshakeRegistry);
                if (legacy.FeatureNegotiation is not null || legacy.SelectedFeatures is not null) throw NcpSchemaBatchNegotiation.Invalid();
                return legacy;
            }
            try { NcpCanonicalWire.ValidateEnvelopeNames(body); }
            catch (NpsException) { throw NcpSchemaBatchNegotiation.Invalid(); }
            return NcpCanonicalWire.DecodeBody(header.FrameType, body, HandshakeRegistry);
        }
        return header.EncodingTier switch
        {
            EncodingTier.Json => JsonCodec.Decode(header.FrameType, payload, HandshakeRegistry),
            EncodingTier.MsgPack => MsgPackCodec.Decode(header.FrameType, payload, HandshakeRegistry),
            EncodingTier.BinaryVector => BinaryVectorCodec.Decode(header.FrameType, payload, HandshakeRegistry),
            _ => throw new NpsCodecException($"Unsupported handshake encoding tier: {header.EncodingTier} (0x{(byte)header.EncodingTier:X2}).")
        };
    }

    /// <summary>
    /// Reads a frame header from <paramref name="stream"/>, peeking the EXT flag
    /// to determine whether to read 4 or 8 bytes total.
    /// </summary>
    internal static async Task<(FrameHeader header, byte[] raw)> ReadFrameHeaderAsync(
        Stream stream, CancellationToken ct)
    {
        // Always read 2 bytes first to detect EXT flag
        var peek = new byte[2];
        await stream.ReadExactlyAsync(peek, ct).ConfigureAwait(false);

        bool ext = ((FrameFlags)peek[1] & FrameFlags.Ext) != 0;
        int remaining = ext ? FrameHeader.ExtendedSize - 2 : FrameHeader.DefaultSize - 2;

        var rest = new byte[remaining];
        await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);

        var raw = new byte[peek.Length + rest.Length];
        peek.CopyTo(raw, 0);
        rest.CopyTo(raw, peek.Length);

        return (FrameHeader.Parse(raw), raw);
    }
}
