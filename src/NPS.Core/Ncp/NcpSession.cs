// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.IO;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Alpha20;
using NPS.Core.Exceptions;

namespace NPS.Core.Ncp;

/// <summary>
/// A live NCP native-mode session established after a successful handshake.
/// Wraps the underlying TCP connection and exposes the negotiated parameters.
/// Upper-layer protocols (NWP, NIP, …) obtain the raw stream via <see cref="GetStream"/>.
/// </summary>
public sealed class NcpSession : IAsyncDisposable
{
    private readonly TcpClient     _tcp;
    private readonly Stream        _stream;
    private bool _disposed;
    private SchemaBatchReceiver? _schemaBatchReceiver;

    /// <summary>Capabilities the server advertised during the handshake.</summary>
    public NcpHandshakeCapsFrame ServerCaps { get; }

    /// <summary>Encoding policy negotiated during the handshake.</summary>
    public NcpEncodingPolicy EncodingPolicy { get; }

    /// <summary>Validated selections scoped to this connection; grants no source/resource authority.</summary>
    public IReadOnlyList<FeatureSelection> SelectedFeatures { get; }

    /// <summary>Stable default encoding tier negotiated during the handshake.</summary>
    public EncodingTier NegotiatedTier => EncodingPolicy.DefaultTier;

    /// <summary><c>true</c> while the underlying TCP connection is still open.</summary>
    public bool IsConnected => _tcp.Connected;

    internal NcpSession(
        TcpClient             tcp,
        Stream                stream,
        NcpHandshakeCapsFrame serverCaps,
        NcpEncodingPolicy     encodingPolicy,
        IReadOnlyList<FeatureSelection>? selectedFeatures = null)
    {
        _tcp            = tcp;
        _stream         = stream;
        ServerCaps      = serverCaps;
        EncodingPolicy  = encodingPolicy;
        SelectedFeatures = Array.AsReadOnly((selectedFeatures ?? []).ToArray());
    }

    /// <summary>
    /// Returns the authenticated transport stream for upper-layer protocol use.
    /// The stream is owned by this session — do not dispose it directly.
    /// </summary>
    public Stream GetStream() => _stream;

    /// <summary>
    /// Encodes a schema-batch frame only after exact session selection, using the
    /// canonical profile fields and negotiated tier/payload. Host source policy remains required.
    /// </summary>
    public byte[] EncodeSchemaBatchFrame(IFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SelectedFeatures.Contains(new FeatureSelection(SchemaBatchReceiver.FeatureId, 1)))
            throw new NpsException("Schema-batch revision is not selected.", NpsStatusCodes.ClientConflict, NcpErrorCodes.FeatureRequiredUnsupported);
        return SchemaBatchFrameCodec.Encode(frame, NegotiatedTier, Math.Min(65536U, ServerCaps.MaxFramePayload ?? FrameHeader.DefaultMaxPayload));
    }

    /// <summary>
    /// Creates a local receiver only after actual session selection. Host still owns
    /// current source/identity policy, tier checks, serialized ingress and disposal.
    /// The negotiated payload ceiling is always applied, including when local limits are omitted.
    /// </summary>
    public SchemaBatchReceiver CreateSchemaBatchReceiver(SchemaBatchLimits? limits = null, TimeProvider? timeProvider = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_schemaBatchReceiver is { IsTerminal: false })
            throw new InvalidOperationException("This reference session already owns a live schema batch receiver.");
        var local = limits ?? new();
        var maximum = Math.Min(local.MaxPayloadBytes, ServerCaps.MaxFramePayload ?? FrameHeader.DefaultMaxPayload);
        return _schemaBatchReceiver = new(SelectedFeatures, local with { MaxPayloadBytes = maximum }, timeProvider)
            { SessionEncodingPolicy = EncodingPolicy };
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _schemaBatchReceiver?.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}
