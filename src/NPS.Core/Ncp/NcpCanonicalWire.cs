// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text.Json;
using MessagePack;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;
using NPS.Core.Serialization;

namespace NPS.Core.Ncp;

// Explicit optional-profile projection. The unselected alpha.19 codecs/formatters
// retain their existing behavior. Production MessagePack always uses the library.
internal static class NcpCanonicalWire
{
    private static readonly FrameRegistry Registry = FrameRegistry.CreateDefault();
    private static readonly Tier1JsonCodec Json = new();
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithResolver(NpsMessagePackResolver.Instance).WithSecurity(MessagePackSecurity.UntrustedData);

    internal static byte[] Encode(IFrame frame, EncodingTier tier, uint maxPayload)
    {
        if (tier is not (EncodingTier.Json or EncodingTier.MsgPack))
            throw new NpsEncodingUnsupportedException("This profile supports JSON and MessagePack.");
        var source = JsonElement.Parse(Json.Encode(frame, Registry));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteEnvelope(writer, source);
        var payload = tier == EncodingTier.Json ? buffer.WrittenSpan.ToArray()
            : MessagePackSerializer.Serialize(JsonElement.Parse(buffer.WrittenSpan), Options);
        if (payload.Length > maxPayload)
            throw new NpsException("Profile payload exceeds bound.", NpsStatusCodes.LimitPayload, NcpErrorCodes.FramePayloadTooLarge);
        var flags = (FrameFlags)(byte)tier;
        if (frame is not StreamFrame stream || stream.IsLast) flags |= FrameFlags.Final;
        if (payload.Length > FrameHeader.DefaultMaxPayload) flags |= FrameFlags.Ext;
        var header = new FrameHeader(frame.FrameType, flags, (uint)payload.Length);
        var wire = new byte[header.HeaderSize + payload.Length];
        header.WriteTo(wire);
        payload.CopyTo(wire, header.HeaderSize);
        return wire;
    }

    internal static JsonElement ReadBody(ReadOnlySpan<byte> payload, EncodingTier tier) =>
        tier == EncodingTier.Json ? JsonElement.Parse(payload)
            : MessagePackSerializer.Deserialize<JsonElement>(payload.ToArray(), Options);

    internal static IFrame DecodeBody(FrameType type, JsonElement body, FrameRegistry? registry = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) body.WriteTo(writer);
        return Json.Decode(type, buffer.WrittenSpan, registry ?? Registry);
    }

    internal static void ValidateEnvelopeNames(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var member in body.EnumerateObject())
            if (member.Name.Length == 0 || member.Name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
                throw Invalid();
        if (body.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
        {
            ValidateEnvelopeNames(schema);
            if (schema.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                foreach (var field in fields.EnumerateArray()) if (field.ValueKind == JsonValueKind.Object) ValidateEnvelopeNames(field);
        }
        if (body.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.Array)
            foreach (var operation in patch.EnumerateArray()) if (operation.ValueKind == JsonValueKind.Object) ValidateEnvelopeNames(operation);
        if (body.TryGetProperty("inline_anchor", out var inline) && inline.ValueKind == JsonValueKind.Object) ValidateEnvelopeNames(inline);
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, JsonElement source)
    {
        writer.WriteStartObject();
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name is "frame_type" or "preferred_tier") continue;
            writer.WritePropertyName(property.Name);
            if (property.Name == "inline_anchor" && property.Value.ValueKind == JsonValueKind.Object) WriteEnvelope(writer, property.Value);
            else property.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static NpsException Invalid() => new("Noncanonical profile envelope.",
        NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
}
