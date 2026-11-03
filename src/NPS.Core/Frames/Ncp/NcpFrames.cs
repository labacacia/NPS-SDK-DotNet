// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;

namespace NPS.Core.Frames.Ncp;

// ── AnchorFrame (0x01) ───────────────────────────────────────────────────────

/// <summary>
/// Schema anchor frame. Carries the full schema definition on first contact;
/// subsequent messages reference it by <see cref="AnchorId"/> only (NPS-1 §4.1).
/// <para>Schema reuse affects transport representation; provider token savings require separate measurement.</para>
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record AnchorFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Anchor;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>
    /// SHA-256 digest of the RFC 8785 JCS canonical schema JSON,
    /// formatted as <c>sha256:{64 lowercase hex chars}</c> (NPS-1 §4.1).
    /// Use <c>AnchorIdComputer.Compute(schema)</c> to generate this value.
    /// </summary>
    [JsonPropertyName("anchor_id")]
    public required string AnchorId { get; init; }

    /// <summary>Full schema definition — transmitted once per session.</summary>
    public required FrameSchema Schema { get; init; }

    /// <summary>
    /// Cache TTL in seconds. Default 3600 (1 hour). Use 0 to disable caching (NPS-1 §4.1).
    /// </summary>
    [JsonPropertyName("ttl")]
    public uint Ttl { get; init; } = 3600;
}

// ── DiffFrame (0x02) ─────────────────────────────────────────────────────────

/// <summary>
/// Incremental diff frame. Carries only changed fields as RFC 6902 JSON Patch operations
/// or a compact binary bitset, referencing the base schema via <see cref="AnchorRef"/> (NPS-1 §4.2).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DiffFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Diff;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>anchor_id of the base schema this diff applies to.</summary>
    [JsonPropertyName("anchor_ref")]
    public required string AnchorRef { get; init; }

    /// <summary>Version sequence number of the data this diff is based on.</summary>
    [JsonPropertyName("base_seq")]
    public required ulong BaseSeq { get; init; }

    /// <summary>
    /// Patch format: <c>"json_patch"</c> (default) or <c>"binary_bitset"</c> (Tier-2 only).
    /// See NPS-1 §4.2 for the binary_bitset layout.
    /// </summary>
    [JsonPropertyName("patch_format")]
    public string PatchFormat { get; init; } = NcpPatchFormat.JsonPatch;

    /// <summary>
    /// RFC 6902 JSON Patch operations (when <see cref="PatchFormat"/> is <c>"json_patch"</c>).
    /// Each element has <c>op</c>, <c>path</c>, and optionally <c>value</c>.
    /// </summary>
    public IReadOnlyList<JsonPatchOperation>? Patch { get; init; }

    /// <summary>
    /// Raw binary bitset patch bytes (when <see cref="PatchFormat"/> is <c>"binary_bitset"</c>).
    /// Layout: changed-fields bitset (ceil(N/8) bytes) followed by MsgPack-encoded new values.
    /// MUST only be used in Tier-2 (MsgPack) frames.
    /// </summary>
    [JsonPropertyName("binary_patch")]
    public byte[]? BinaryBitset { get; init; }

    /// <summary>Optional ID of the entity being patched (e.g. <c>"product:1001"</c>).</summary>
    [JsonPropertyName("entity_id")]
    public string? EntityId { get; init; }
}

/// <summary>
/// A single RFC 6902 JSON Patch operation (NPS-1 §4.2).
/// </summary>
/// <param name="Op">
/// Operation type: <c>add</c>, <c>remove</c>, <c>replace</c>, <c>move</c>, <c>copy</c>, <c>test</c>.
/// </param>
/// <param name="Path">JSON Pointer (RFC 6901) to the target location, e.g. <c>"/price"</c>.</param>
/// <param name="Value">New value for <c>add</c> / <c>replace</c> / <c>test</c>; absent for <c>remove</c>.</param>
/// <param name="From">Source location for <c>move</c> / <c>copy</c>.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record JsonPatchOperation(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("from")] string? From = null);

// ── StreamFrame (0x03) ───────────────────────────────────────────────────────

/// <summary>
/// Streaming chunk frame. Used for large datasets or real-time push.
/// Chunks are ordered by <see cref="Seq"/> and reassembled by the receiver (NPS-1 §4.3).
/// Back-pressure is signalled via <see cref="WindowSize"/>.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record StreamFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Stream;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Unique identifier for this stream. UUID v4 format.</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>Monotonically increasing chunk sequence number, 0-based.</summary>
    [JsonPropertyName("seq")]
    public required uint Seq { get; init; }

    /// <summary><c>true</c> when this is the final chunk. Corresponds to header FINAL flag.</summary>
    [JsonPropertyName("is_last")]
    public required bool IsLast { get; init; }

    /// <summary>
    /// Schema anchor reference. Carried in the first chunk; subsequent chunks may omit it.
    /// </summary>
    [JsonPropertyName("anchor_ref")]
    public string? AnchorRef { get; init; }

    /// <summary>Data rows for this chunk, each conforming to the referenced schema.</summary>
    public required IReadOnlyList<JsonElement> Data { get; init; }

    /// <summary>
    /// Back-pressure window size. Sender declares how many chunks it will send before
    /// waiting for a receiver ACK. Optional (NPS-1 §4.3).
    /// </summary>
    [JsonPropertyName("window_size")]
    public uint? WindowSize { get; init; }

    /// <summary>
    /// Non-null when the stream terminates abnormally. Forces <see cref="IsLast"/> to <c>true</c>.
    /// Error code format: <c>NCP-STREAM-*</c> (NPS-1 §6).
    /// </summary>
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }
}

// ── CapsFrame (0x04) ─────────────────────────────────────────────────────────

/// <summary>
/// Capsule frame — full response envelope. References a cached anchor schema
/// and carries the complete result set, with optional cursor for pagination (NPS-1 §4.4).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record CapsFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Caps;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>anchor_id of the schema that describes each record in <see cref="Data"/>.</summary>
    [JsonPropertyName("anchor_ref")]
    public required string AnchorRef { get; init; }

    /// <summary>Total number of items in <see cref="Data"/>. MUST equal <c>Data.Count</c>.</summary>
    public required uint Count { get; init; }

    /// <summary>Result rows — each record conforms to the schema referenced by <see cref="AnchorRef"/>.</summary>
    public required IReadOnlyList<JsonElement> Data { get; init; }

    /// <summary>
    /// Opaque Base64-URL encoded cursor for fetching the next page.
    /// <c>null</c> indicates this is the last page.
    /// </summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    /// <summary>Estimated token cost of this response (informational, for Agent planning).</summary>
    [JsonPropertyName("token_est")]
    public uint? TokenEst { get; init; }

    /// <summary><c>true</c> when this response was served from the server-side cache.</summary>
    [JsonPropertyName("cached")]
    public bool? Cached { get; init; }

    /// <summary>
    /// Correlates this unary response with the originating request. For synchronous
    /// NWP operations, producers SHOULD copy the request frame's <c>request_id</c>.
    /// </summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>Tokenizer identifier used for <see cref="TokenEst"/> calculation (NPS-1 §4.4).</summary>
    [JsonPropertyName("tokenizer_used")]
    public string? TokenizerUsed { get; init; }

    /// <summary>
    /// When the referenced schema has been updated, Node SHOULD inline the latest
    /// <see cref="AnchorFrame"/> here so the Agent can update its cache without an extra RTT (NPS-1 §5.4).
    /// Agent MUST verify the <c>anchor_id</c> (JCS + SHA-256) before accepting the new schema.
    /// </summary>
    [JsonPropertyName("inline_anchor")]
    public AnchorFrame? InlineAnchor { get; init; }
}

// ── ErrorFrame (0xFE) ────────────────────────────────────────────────────────

/// <summary>
/// Unified error frame shared across all NPS protocol layers (NPS-1 §4.6).
/// In native transport mode, errors are conveyed via this frame type.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ErrorFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Error;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>NPS status code, e.g. <c>NPS-CLIENT-NOT-FOUND</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Protocol-level error code, e.g. <c>NCP-ANCHOR-NOT-FOUND</c>.</summary>
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    /// <summary>Human-readable error description.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Structured error details (e.g. <c>anchor_ref</c>, <c>stream_id</c>).</summary>
    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }
}

// ── HelloFrame (0x06) ────────────────────────────────────────────────────────

/// <summary>
/// Native-mode client handshake frame. The Agent MUST send this as the very first frame
/// after opening a TCP/QUIC connection; the Node replies with a <see cref="CapsFrame"/>.
/// Not used in HTTP mode (NPS-1 §4.6).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record HelloFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Hello;
    /// <summary>Tier-1 JSON is recommended during handshake (encoding not yet negotiated).</summary>
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.Json;

    /// <summary>Optional exact feature negotiation version; no support is enabled by presence.</summary>
    [JsonPropertyName("feature_negotiation")]
    public uint? FeatureNegotiation { get; init; }

    /// <summary>Bounded raw CR-0012 offer; validated before accepting an experimental session.</summary>
    [JsonPropertyName("feature_offer")]
    public JsonElement? FeatureOffer { get; init; }

    /// <summary>Offered feature IDs whose absence must reject this handshake.</summary>
    [JsonPropertyName("required_features")]
    public IReadOnlyList<string>? RequiredFeatures { get; init; }

    /// <summary>Highest NPS version the client supports, e.g. <c>"0.4"</c>.</summary>
    [JsonPropertyName("nps_version")]
    public required string NpsVersion { get; init; }

    /// <summary>
    /// Lowest NPS version the client accepts. Defaults to <see cref="NpsVersion"/> when omitted.
    /// Server MUST reject the connection with <c>NCP-VERSION-INCOMPATIBLE</c> if its version
    /// is below this value.
    /// </summary>
    [JsonPropertyName("min_version")]
    public string? MinVersion { get; init; }

    /// <summary>
    /// Ordered list of supported encoding formats, highest priority first.
    /// E.g. <c>["msgpack", "json"]</c>.
    /// </summary>
    [JsonPropertyName("supported_encodings")]
    public required IReadOnlyList<string> SupportedEncodings { get; init; }

    /// <summary>
    /// Upper-layer NPS protocols the client can speak, e.g. <c>["ncp", "nwp", "nip"]</c>.
    /// </summary>
    [JsonPropertyName("supported_protocols")]
    public required IReadOnlyList<string> SupportedProtocols { get; init; }

    /// <summary>Agent NID in <c>urn:nps:agent:{domain}:{id}</c> format. Optional.</summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    /// <summary>
    /// Maximum frame payload (bytes) this client will accept.
    /// Default: <see cref="FrameHeader.DefaultMaxPayload"/> (65 535).
    /// </summary>
    [JsonPropertyName("max_frame_payload")]
    public uint MaxFramePayload { get; init; } = FrameHeader.DefaultMaxPayload;

    /// <summary>Whether the client supports the 8-byte extended frame header (EXT=1). Default: false.</summary>
    [JsonPropertyName("ext_support")]
    public bool ExtSupport { get; init; } = false;

    /// <summary>Maximum number of concurrent streams the client can handle. Default: 32.</summary>
    [JsonPropertyName("max_concurrent_streams")]
    public uint MaxConcurrentStreams { get; init; } = 32;

    /// <summary>
    /// E2E encryption algorithms the client supports, in preference order (NPS-1 §7.4).
    /// E.g. <c>["aes-256-gcm", "chacha20-poly1305"]</c>.
    /// Empty or null means E2E encryption is not supported.
    /// </summary>
    [JsonPropertyName("e2e_enc_algorithms")]
    public IReadOnlyList<string>? E2EEncAlgorithms { get; init; }
}

// ── AlignFrame (0x05) — Deprecated ───────────────────────────────────────────

/// <summary>
/// Multi-AI state alignment frame (NCP v0.1 layer).
/// <para>
/// ⚠️ <b>Deprecated in NCP v0.2.</b> Use NOP <c>AlignStream (0x43)</c> instead,
/// which adds task context and NID binding. This type will be removed in NPS v1.0 (NPS-1 §4.5).
/// </para>
/// </summary>
[Obsolete("AlignFrame (0x05) is deprecated in NCP v0.2. Use NOP AlignStream (0x43) instead. Will be removed in NPS v1.0.")]
[MessagePackObject(keyAsPropertyName: true)]
public sealed record AlignFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Align;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Shared state snapshot as an arbitrary JSON object.</summary>
    public required JsonElement State { get; init; }

    /// <summary>Participating Agent NIDs.</summary>
    public required IReadOnlyList<string> Participants { get; init; }

    /// <summary>Consensus round index.</summary>
    [JsonPropertyName("round")]
    public uint Round { get; init; }
}

// ── NcpHandshakeCapsFrame (0x04, native-mode handshake response) ──────────────

/// <summary>
/// Server's capability response to <see cref="HelloFrame"/> in native mode (NPS-1 §4.6).
/// Carries the server NID and its capability list. Uses frame type 0x04 (Caps) on the wire.
/// The response frame header determines the stable default encoding; optional payload
/// fields echo the full enabled encoding policy for extensions such as BinaryVector.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record NcpHandshakeCapsFrame : IFrame
{
    [IgnoreMember]
    public FrameType FrameType => FrameType.Caps;
    [IgnoreMember]
    public EncodingTier PreferredTier => EncodingTier.Json;

    /// <summary>CR-0012 acknowledgment, scoped to this handshake only.</summary>
    [JsonPropertyName("feature_negotiation")]
    public uint? FeatureNegotiation { get; init; }

    /// <summary>Exact admitted feature selections; never renegotiated through application Caps.</summary>
    [JsonPropertyName("selected_features")]
    public JsonElement? SelectedFeatures { get; init; }

    /// <summary>The server's NID in <c>urn:nps:agent:{domain}:{id}</c> format.</summary>
    [JsonPropertyName("node_id")]
    public required string NodeId { get; init; }

    /// <summary>Protocols and capabilities the server supports, e.g. <c>["ncp","nwp","nip"]</c>.</summary>
    [JsonPropertyName("caps")]
    public required IReadOnlyList<string> Caps { get; init; }

    /// <summary>Stable default encoding selected for ordinary session frames.</summary>
    [JsonPropertyName("negotiated_encoding")]
    public string? NegotiatedEncoding { get; init; }

    /// <summary>All encodings enabled by the negotiated policy, including optional extensions.</summary>
    [JsonPropertyName("enabled_encodings")]
    public IReadOnlyList<string>? EnabledEncodings { get; init; }

    /// <summary>Highest protocol version selected from the peer ranges.</summary>
    [JsonPropertyName("session_version")]
    public string? SessionVersion { get; init; }

    /// <summary>Negotiated higher-layer protocol intersection in client preference order.</summary>
    [JsonPropertyName("supported_protocols")]
    public IReadOnlyList<string>? SupportedProtocols { get; init; }

    /// <summary>Negotiated maximum payload bytes per frame.</summary>
    [JsonPropertyName("max_frame_payload")]
    public uint? MaxFramePayload { get; init; }

    /// <summary>Whether both peers support the extended frame header.</summary>
    [JsonPropertyName("ext_support")]
    public bool? ExtSupport { get; init; }

    /// <summary>Negotiated maximum number of concurrent streams.</summary>
    [JsonPropertyName("max_concurrent_streams")]
    public uint? MaxConcurrentStreams { get; init; }

    /// <summary>Optional anchor reference for the server's first offered schema.</summary>
    [JsonPropertyName("anchor_ref")]
    public string? AnchorRef { get; init; }

    /// <summary>Optional additional metadata (implementation-defined).</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}
