// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core.Exceptions;

/// <summary>Base exception for all NPS protocol errors.</summary>
public class NpsException : Exception
{
    /// <summary>NPS status code (e.g. <c>NPS-CLIENT-NOT-FOUND</c>). Null for non-wire errors.</summary>
    public string? NpsStatusCode { get; }

    /// <summary>Protocol-level error code (e.g. <c>NCP-ANCHOR-NOT-FOUND</c>). Null for non-wire errors.</summary>
    public string? ProtocolErrorCode { get; }

    public NpsException(string message) : base(message) { }
    public NpsException(string message, Exception inner) : base(message, inner) { }

    public NpsException(string message, string npsStatusCode, string protocolErrorCode)
        : base(message)
    {
        NpsStatusCode = npsStatusCode;
        ProtocolErrorCode = protocolErrorCode;
    }

    public NpsException(string message, Exception inner, string npsStatusCode, string protocolErrorCode)
        : base(message, inner)
    {
        NpsStatusCode = npsStatusCode;
        ProtocolErrorCode = protocolErrorCode;
    }
}

/// <summary>Thrown when a frame cannot be parsed or its structure is invalid.</summary>
public sealed class NpsFrameException : NpsException
{
    public NpsFrameException(string message) : base(message) { }
    public NpsFrameException(string message, Exception inner) : base(message, inner) { }
    public NpsFrameException(string message, string npsStatusCode, string protocolErrorCode)
        : base(message, npsStatusCode, protocolErrorCode) { }
}

/// <summary>Thrown when encoding or decoding fails at the codec layer.</summary>
public sealed class NpsCodecException : NpsException
{
    public NpsCodecException(string message) : base(message) { }
    public NpsCodecException(string message, Exception inner) : base(message, inner) { }
    public NpsCodecException(string message, string npsStatusCode, string protocolErrorCode)
        : base(message, npsStatusCode, protocolErrorCode) { }
    public NpsCodecException(string message, Exception inner, string npsStatusCode, string protocolErrorCode)
        : base(message, inner, npsStatusCode, protocolErrorCode) { }
}

/// <summary>
/// Thrown when a frame uses an encoding tier outside the negotiated session policy.
/// Error code: <c>NCP-ENCODING-UNSUPPORTED</c>.
/// </summary>
public sealed class NpsEncodingUnsupportedException : NpsException
{
    public NpsEncodingUnsupportedException(string message)
        : base(message, NpsStatusCodes.ServerEncodingUnsupported, NcpErrorCodes.EncodingUnsupported)
    {
    }
}

/// <summary>
/// Thrown when an <c>anchor_ref</c> is referenced but not found in the cache.
/// Error code: <c>NCP-ANCHOR-NOT-FOUND</c>.
/// </summary>
public sealed class NpsAnchorNotFoundException : NpsException
{
    public string AnchorId { get; }

    public NpsAnchorNotFoundException(string anchorId)
        : base($"AnchorFrame not found in cache: {anchorId}. " +
               "Peer must resend AnchorFrame (0x01) before referencing it.",
               NpsStatusCodes.ClientNotFound, "NCP-ANCHOR-NOT-FOUND")
    {
        AnchorId = anchorId;
    }
}

/// <summary>
/// Thrown when an incoming <c>AnchorFrame</c> carries a schema that conflicts with an
/// existing cache entry. Error code: <c>NCP-ANCHOR-SCHEMA-INVALID</c>.
/// </summary>
public sealed class NpsAnchorPoisonException : NpsException
{
    public string AnchorId { get; }

    public NpsAnchorPoisonException(string anchorId)
        : base($"Anchor poisoning detected for {anchorId}: " +
               "incoming schema does not match the cached schema for the same anchor_id.",
               NpsStatusCodes.ClientBadFrame, "NCP-ANCHOR-SCHEMA-INVALID")
    {
        AnchorId = anchorId;
    }
}

/// <summary>
/// Thrown for errors involving a <c>StreamFrame</c> sequence.
/// </summary>
public sealed class NpsStreamException : NpsException
{
    public string StreamId { get; }

    /// <summary>NPS error code, e.g. <c>NCP-STREAM-SEQ-GAP</c>.</summary>
    public string ErrorCode { get; }

    public NpsStreamException(string streamId, string errorCode, string message)
        : base(message)
    {
        StreamId  = streamId;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thrown when a HelloFrame <c>min_version</c> is higher than the server supports.
/// Error code: <c>NCP-VERSION-INCOMPATIBLE</c>.
/// </summary>
public sealed class NpsVersionIncompatibleException : NpsException
{
    public string ClientMinVersion  { get; }
    public string ServerNpsVersion  { get; }

    public NpsVersionIncompatibleException(string clientMinVersion, string serverNpsVersion)
        : base($"NPS version incompatible: client requires >= {clientMinVersion}, server supports {serverNpsVersion}.",
               NpsStatusCodes.ProtoVersionIncompatible, NcpErrorCodes.VersionIncompatible)
    {
        ClientMinVersion = clientMinVersion;
        ServerNpsVersion = serverNpsVersion;
    }
}

/// <summary>
/// Thrown when a <see cref="Frames.Ncp.DiffFrame"/> uses <c>binary_bitset</c> format
/// but the receiver does not support it.
/// Error code: <c>NCP-DIFF-FORMAT-UNSUPPORTED</c>.
/// </summary>
public sealed class NpsDiffFormatUnsupportedException : NpsException
{
    public string PatchFormat { get; }

    public NpsDiffFormatUnsupportedException(string patchFormat)
        : base($"DiffFrame patch_format '{patchFormat}' is not supported by this peer.",
               NpsStatusCodes.ClientBadFrame, NcpErrorCodes.DiffFormatUnsupported)
    {
        PatchFormat = patchFormat;
    }
}

/// <summary>
/// Thrown when an <c>anchor_ref</c> is known but the Schema has since been updated.
/// Node SHOULD attach <c>inline_anchor</c> to the response.
/// Error code: <c>NCP-ANCHOR-STALE</c>.
/// </summary>
public sealed class NpsAnchorStaleException : NpsException
{
    public string AnchorId { get; }

    public NpsAnchorStaleException(string anchorId)
        : base($"AnchorFrame with id {anchorId} is stale; schema has been updated. " +
               "Check the inline_anchor field in the response for the latest schema.",
               NpsStatusCodes.ClientConflict, NcpErrorCodes.AnchorStale)
    {
        AnchorId = anchorId;
    }
}

/// <summary>
/// Thrown by <see cref="Ncp.NcpPreamble.Validate"/> when a native-mode
/// connection's first 8 bytes do not match the constant preamble
/// <c>b"NPS/1.0\n"</c>. Per NPS-RFC-0001 / NPS-1 §2.6.1, the calling
/// transport SHOULD log the reason, MUST NOT emit an <c>ErrorFrame</c>,
/// and MUST close the connection within 500 ms.
/// Error code: <c>NCP-PREAMBLE-INVALID</c>.
/// </summary>
public sealed class NcpPreambleInvalidException : NpsException
{
    /// <summary>Short, human-readable cause suitable for log lines.</summary>
    public string Reason { get; }

    public NcpPreambleInvalidException(string reason)
        : base($"Native-mode connection preamble invalid: {reason}",
               NpsStatusCodes.ProtoPreambleInvalid, NcpErrorCodes.PreambleInvalid)
    {
        Reason = reason;
    }
}

/// <summary>
/// Thrown when the NCP native-mode handshake fails because the server sent an
/// <see cref="Frames.Ncp.ErrorFrame"/> or an unexpected frame type in response to
/// <see cref="Frames.Ncp.HelloFrame"/>.
/// </summary>
public sealed class NcpHandshakeException : NpsException
{
    /// <summary>Protocol-level error code from the server's ErrorFrame (e.g. <c>NCP-VERSION-INCOMPATIBLE</c>).</summary>
    public string ErrorCode { get; }

    public NcpHandshakeException(string errorCode, string? message)
        : base(message ?? $"NCP handshake failed: {errorCode}",
               errorCode switch
               {
                   NcpErrorCodes.FeatureNegotiationInvalid => NpsStatusCodes.ClientBadFrame,
                   NcpErrorCodes.FeatureRequiredUnsupported => NpsStatusCodes.ClientConflict,
                   _ => NpsStatusCodes.ProtoVersionIncompatible,
               }, errorCode)
    {
        ErrorCode = errorCode;
    }
}
