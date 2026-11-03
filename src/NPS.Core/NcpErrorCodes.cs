// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core;

/// <summary>
/// NCP protocol-level error code constants (NPS-1 §6).
/// Every code maps to an <see cref="NpsStatusCodes"/> transport-level status code.
/// </summary>
public static class NcpErrorCodes
{
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-BAD-FRAME; never implies safe retry.</summary>
    public const string FeatureNegotiationInvalid = "NCP-FEATURE-NEGOTIATION-INVALID";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-CONFLICT; never implies safe retry.</summary>
    public const string FeatureRequiredUnsupported = "NCP-FEATURE-REQUIRED-UNSUPPORTED";
    // ── Anchor errors ─────────────────────────────────────────────────────────
    /// <summary>anchor_ref references a Schema unknown to this Node. → NPS-CLIENT-NOT-FOUND</summary>
    public const string AnchorNotFound        = "NCP-ANCHOR-NOT-FOUND";

    /// <summary>AnchorFrame carries a malformed Schema. → NPS-CLIENT-BAD-FRAME</summary>
    public const string AnchorSchemaInvalid   = "NCP-ANCHOR-SCHEMA-INVALID";

    /// <summary>Same anchor_id received with a different Schema (poisoning defence). → NPS-CLIENT-CONFLICT</summary>
    public const string AnchorIdMismatch      = "NCP-ANCHOR-ID-MISMATCH";

    /// <summary>
    /// anchor_ref is known but the Schema has been updated.
    /// Node SHOULD attach <c>inline_anchor</c> to the CapsFrame response. → NPS-CLIENT-CONFLICT
    /// </summary>
    public const string AnchorStale           = "NCP-ANCHOR-STALE";

    // ── Frame errors ──────────────────────────────────────────────────────────
    /// <summary>Frame type byte is not recognised. → NPS-CLIENT-BAD-FRAME</summary>
    public const string FrameUnknownType      = "NCP-FRAME-UNKNOWN-TYPE";

    /// <summary>Payload exceeds the negotiated max_frame_payload. → NPS-LIMIT-PAYLOAD</summary>
    public const string FramePayloadTooLarge  = "NCP-FRAME-PAYLOAD-TOO-LARGE";

    /// <summary>Reserved bits in Flags are non-zero. → NPS-CLIENT-BAD-FRAME</summary>
    public const string FrameFlagsInvalid     = "NCP-FRAME-FLAGS-INVALID";

    /// <summary>Tier-3 BinaryVector payload is malformed. → NPS-CLIENT-BAD-FRAME</summary>
    public const string BinaryVectorMalformed = "NCP-BINARY-VECTOR-MALFORMED";

    /// <summary>Tier-3 BinaryVector marker dimension does not match its segment. → NPS-CLIENT-BAD-FRAME</summary>
    public const string BinaryVectorDimMismatch = "NCP-BINARY-VECTOR-DIM-MISMATCH";

    /// <summary>Tier-3 BinaryVector marker references a missing segment. → NPS-CLIENT-BAD-FRAME</summary>
    public const string BinaryVectorIndexInvalid = "NCP-BINARY-VECTOR-INDEX-INVALID";

    /// <summary>Tier-3 BinaryVector marker uses an unsupported dtype. → NPS-CLIENT-BAD-FRAME</summary>
    public const string BinaryVectorDtypeUnsupported = "NCP-BINARY-VECTOR-DTYPE-UNSUPPORTED";

    /// <summary>Tier-3 BinaryVector vector segment is truncated. → NPS-CLIENT-BAD-FRAME</summary>
    public const string BinaryVectorTruncated = "NCP-BINARY-VECTOR-TRUNCATED";

    /// <summary>Native-mode TLS client-certificate NID does not match the session IdentFrame NID (RFC-0006 §6.3–6.4).</summary>
    public const string NidMismatch           = "NCP-NID-MISMATCH";

    // ── Stream errors ─────────────────────────────────────────────────────────
    /// <summary>StreamFrame sequence number is non-contiguous. → NPS-STREAM-SEQ-GAP</summary>
    public const string StreamSeqGap          = "NCP-STREAM-SEQ-GAP";

    /// <summary>stream_id references a stream that does not exist. → NPS-STREAM-NOT-FOUND</summary>
    public const string StreamNotFound        = "NCP-STREAM-NOT-FOUND";

    /// <summary>Concurrent stream count exceeds the negotiated cap. → NPS-STREAM-LIMIT</summary>
    public const string StreamLimitExceeded   = "NCP-STREAM-LIMIT-EXCEEDED";

    /// <summary>Sender continued transmitting after window reached zero. → NPS-STREAM-LIMIT</summary>
    public const string StreamWindowOverflow  = "NCP-STREAM-WINDOW-OVERFLOW";

    // ── Encoding / version errors ─────────────────────────────────────────────
    /// <summary>Requested encoding tier is not supported by this peer. → NPS-SERVER-ENCODING-UNSUPPORTED</summary>
    public const string EncodingUnsupported   = "NCP-ENCODING-UNSUPPORTED";

    /// <summary>
    /// DiffFrame uses <c>binary_bitset</c> but the receiver does not support it. → NPS-CLIENT-BAD-FRAME
    /// </summary>
    public const string DiffFormatUnsupported = "NCP-DIFF-FORMAT-UNSUPPORTED";

    /// <summary>
    /// Client's min_version is higher than the Server's supported version. → NPS-PROTO-VERSION-INCOMPATIBLE
    /// </summary>
    public const string VersionIncompatible   = "NCP-VERSION-INCOMPATIBLE";

    // ── E2E encryption errors ─────────────────────────────────────────────────
    /// <summary>ENC=1 received but E2E encryption was not negotiated. → NPS-CLIENT-BAD-FRAME</summary>
    public const string EncNotNegotiated      = "NCP-ENC-NOT-NEGOTIATED";

    /// <summary>E2E Auth Tag verification failed (possible tampering). → NPS-CLIENT-BAD-FRAME</summary>
    public const string EncAuthFailed         = "NCP-ENC-AUTH-FAILED";

    // ── Connection-level errors ───────────────────────────────────────────────
    /// <summary>
    /// Native-mode connection opened with bytes other than the constant
    /// preamble <c>b"NPS/1.0\n"</c>. Server closes silently within 500 ms;
    /// no ErrorFrame is emitted on the wire (NPS-RFC-0001 / NPS-1 §2.6.1).
    /// → NPS-PROTO-PREAMBLE-INVALID
    /// </summary>
    public const string PreambleInvalid       = "NCP-PREAMBLE-INVALID";

    /// <summary>QUIC 0-RTT data is rejected until the handshake is confirmed. → NPS-PROTO-VERSION-INCOMPATIBLE</summary>
    public const string EarlyDataRejected     = "NCP-EARLY-DATA-REJECTED";
}
