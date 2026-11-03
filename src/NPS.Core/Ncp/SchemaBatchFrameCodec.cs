// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Anchoring;
using NPS.Core.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace NPS.Core.Ncp;

/// <summary>
/// Explicit candidate-profile codec using the same canonical snake_case envelope
/// fields in JSON and MessagePack. It neither negotiates nor grants authority.
/// Unselected base codecs remain unchanged. Hosts must bound transport allocation.
/// </summary>
public static class SchemaBatchFrameCodec
{
    /// <summary>
    /// Computes the profile's ordered schema JCS/SHA-256 identity. The materialized
    /// field shape includes nullable=false and omits null semantic annotations.
    /// Unicode is preserved without normalization; invalid UTF-16 rejects. Legacy
    /// unselected AnchorIdComputer.Compute escaping remains unchanged.
    /// </summary>
    public static string ComputeAnchorId(FrameSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var canonical = AnchorIdComputer.CanonicalJson(schema, JcsString.Append);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Encodes an existing Anchor/Caps/Stream/Diff in the explicitly selected profile.</summary>
    public static byte[] Encode(IFrame frame, EncodingTier tier, uint maxPayload = 65536)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame is not (AnchorFrame or CapsFrame or StreamFrame or DiffFrame)) throw Invalid();
        if (maxPayload is 0 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxPayload));
        return NcpCanonicalWire.Encode(frame, tier, maxPayload);
    }

    /// <summary>Validates one complete bounded profile frame before decoding typed fields.</summary>
    public static IFrame Decode(ReadOnlySpan<byte> wire, uint maxPayload = 65536)
    {
        if (maxPayload is 0 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxPayload));
        var header = FrameHeader.Parse(wire);
        if (header.EncodingTier is not (EncodingTier.Json or EncodingTier.MsgPack))
            throw new NpsEncodingUnsupportedException("This profile supports JSON and MessagePack.");
        if (header.PayloadLength > maxPayload)
            throw new NpsException("Profile payload exceeds bound.", NpsStatusCodes.LimitPayload, NcpErrorCodes.FramePayloadTooLarge);
        if ((long)header.HeaderSize + header.PayloadLength != wire.Length || header.IsEncrypted
            || header.FrameType is not (FrameType.Anchor or FrameType.Caps or FrameType.Stream or FrameType.Diff)) throw Invalid();
        SchemaBatchWireGuard.Validate(wire[header.HeaderSize..], header.EncodingTier, header.FrameType);
        var body = NcpCanonicalWire.ReadBody(wire[header.HeaderSize..], header.EncodingTier);
        NcpCanonicalWire.ValidateEnvelopeNames(body);
        var frame = NcpCanonicalWire.DecodeBody(header.FrameType, body);
        if (frame is DiffFrame diff) frame = SchemaBatchDiffWireFields.Restore(diff, body);
        if (header.IsFinal != (frame is not StreamFrame stream || stream.IsLast)) throw Invalid();
        return frame;
    }

    private static NpsException Invalid() => new("Invalid schema-batch frame.",
        NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
}
