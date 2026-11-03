// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.IO;
using System.Net.Security;
using NPS.Core.Alpha20;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>
/// Server-side representation of an inbound NCP connection that has passed the preamble
/// check and sent its <see cref="HelloFrame"/>. Call <see cref="AcceptAsync"/> to complete
/// the handshake, or <see cref="RejectAsync"/> to send an error and close the connection.
/// </summary>
public sealed class NcpServerConnection : IAsyncDisposable
{
    private readonly TcpClient     _tcp;
    private readonly Stream        _stream;
    private readonly NpsFrameCodec _codec;
    private readonly NcpHandshakeProfile _profile;

    /// <summary>The <see cref="HelloFrame"/> sent by the connecting client.</summary>
    public HelloFrame ClientHello { get; }

    internal NcpServerConnection(
        TcpClient     tcp,
        Stream        stream,
        NpsFrameCodec codec,
        HelloFrame    clientHello,
        NcpHandshakeProfile profile)
    {
        _tcp        = tcp;
        _stream     = stream;
        _codec      = codec;
        ClientHello = clientHello;
        _profile    = profile;
    }

    /// <summary>
    /// Sends <paramref name="serverCaps"/> to the client and returns a live <see cref="NcpSession"/>.
    /// The encoding policy is negotiated from the client's <c>SupportedEncodings</c> list.
    /// </summary>
    public async Task<NcpSession> AcceptAsync(
        NcpHandshakeCapsFrame serverCaps,
        CancellationToken     ct = default)
    {
        var negotiation = NcpNativeServerPolicy.Negotiate(_profile, ClientHello);
        if (negotiation.Action != NcpHandshakeAction.Accept)
        {
            var error = negotiation.Error ?? NcpErrorCodes.VersionIncompatible;
            await RejectAsync(new ErrorFrame
            {
                Status = negotiation.Status ?? NpsStatusCodes.ProtoVersionIncompatible,
                Error = error,
                Message = "Native NCP handshake negotiation failed.",
            }, ct).ConfigureAwait(false);
            throw new NcpHandshakeException(error, "Native NCP handshake negotiation failed.");
        }

        IReadOnlyList<FeatureSelection> selected;
        try
        {
            selected = NcpSchemaBatchNegotiation.Select(ClientHello, _profile.SchemaBatchesEnabled);
            if (selected.Count != 0 && _stream is not SslStream { IsAuthenticated: true, IsMutuallyAuthenticated: true })
                throw new NcpHandshakeException(NcpErrorCodes.FeatureRequiredUnsupported, "Schema batch reference requires verified mutual TLS.");
        }
        catch (NcpHandshakeException ex)
        {
            await RejectAsync(new ErrorFrame { Status = ex.ErrorCode == NcpErrorCodes.FeatureRequiredUnsupported
                ? NpsStatusCodes.ClientConflict : NpsStatusCodes.ClientBadFrame,
                Error = ex.ErrorCode, Message = "Native feature negotiation rejected." }, ct).ConfigureAwait(false);
            throw;
        }

        var defaultTier = negotiation.NegotiatedEncoding == "msgpack"
            ? EncodingTier.MsgPack
            : EncodingTier.Json;
        var policy = new NcpEncodingPolicy(
            defaultTier,
            negotiation.EnabledEncodings?.Contains(
                "binary_vector.v1",
                StringComparer.Ordinal) == true);
        var caps = serverCaps with
        {
            FeatureNegotiation = ClientHello.FeatureNegotiation is null ? null : 1,
            SelectedFeatures = ClientHello.FeatureNegotiation is null ? null : NcpSchemaBatchNegotiation.SerializeSelection(selected),
            SessionVersion = negotiation.SessionVersion,
            NegotiatedEncoding = negotiation.NegotiatedEncoding,
            EnabledEncodings = negotiation.EnabledEncodings,
            SupportedProtocols = negotiation.SupportedProtocols,
            MaxFramePayload = negotiation.MaxFramePayload,
            ExtSupport = negotiation.ExtSupport,
            MaxConcurrentStreams = negotiation.MaxConcurrentStreams,
        };
        byte[] wire;
        try
        {
            wire = caps.FeatureNegotiation is null ? _codec.Encode(caps, policy.DefaultTier)
                : NcpCanonicalWire.Encode(caps, policy.DefaultTier, Math.Min(16384U, caps.MaxFramePayload ?? 0));
        }
        catch (NpsException ex) when (caps.FeatureNegotiation is not null && ex.ProtocolErrorCode == NcpErrorCodes.FramePayloadTooLarge)
        {
            await RejectAsync(new ErrorFrame { Status = NpsStatusCodes.ClientBadFrame,
                Error = NcpErrorCodes.FeatureNegotiationInvalid, Message = "Native feature response exceeds bound." }, ct).ConfigureAwait(false);
            throw NcpSchemaBatchNegotiation.Invalid();
        }
        await _stream.WriteAsync(wire, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        return new NcpSession(_tcp, _stream, caps, policy, selected);
    }

    /// <summary>
    /// Sends an <see cref="ErrorFrame"/> to reject the client and closes the connection.
    /// </summary>
    public async Task RejectAsync(ErrorFrame error, CancellationToken ct = default)
    {
        try
        {
            var wire = _codec.Encode(error, EncodingTier.Json);
            await _stream.WriteAsync(wire, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}
