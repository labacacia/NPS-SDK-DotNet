// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text;
using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

// Narrow current-profile integration. Other optional features are never enabled by
// this helper; their required IDs reject. Their independent qualification is retained.
internal static class NcpSchemaBatchNegotiation
{
    private static readonly string[] OfferKeys = ["feature_negotiation", "feature_offer", "required_features"];
    private static readonly FeatureSelection Selected = new(SchemaBatchReceiver.FeatureId, 1);

    internal static FeatureOffer ParseHello(HelloFrame hello)
    {
        try { return FeatureWireParser.ParseOffer(OfferJson(hello)); }
        catch (FeatureFormatException) { throw Invalid(); }
    }

    internal static IReadOnlyList<FeatureSelection> Select(HelloFrame hello, bool locallyEnabled)
    {
        var offer = ParseHello(hello);
        var selected = locallyEnabled && offer.Features.TryGetValue(Selected.Id, out var revisions)
            && revisions.Any(r => r.Revision == Selected.Revision) ? new[] { Selected } : [];
        if (offer.Required.Any(id => !selected.Any(f => f.Id == id))) throw Unsupported();
        return Array.AsReadOnly(selected);
    }

    internal static void ValidateClientOffer(HelloFrame hello, bool locallyEnabled)
    {
        var offer = ParseHello(hello);
        if (!locallyEnabled && offer.Features.ContainsKey(Selected.Id)) throw Unsupported();
    }

    internal static IReadOnlyList<FeatureSelection> ValidateResponse(HelloFrame hello,
        NcpHandshakeCapsFrame caps, bool locallyEnabled)
    {
        var offer = ParseHello(hello);
        if (caps.FeatureNegotiation is null && caps.SelectedFeatures is null)
        {
            if (offer.Required.Count != 0) throw Unsupported();
            return [];
        }
        if (hello.FeatureNegotiation != 1 || caps.FeatureNegotiation != 1
            || caps.SelectedFeatures is not { ValueKind: JsonValueKind.Array } values || values.GetArrayLength() > 32)
            throw Invalid();
        var selections = new List<FeatureSelection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid();
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in item.EnumerateObject()) if (!fields.TryAdd(p.Name, p.Value)) throw Invalid();
            if (fields.Count != 2 || !fields.TryGetValue("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                || !fields.TryGetValue("revision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.Number
                || !revisionValue.TryGetUInt16(out var revision)) throw Invalid();
            var id = idValue.GetString()!;
            // Reuse the strict existing ID/revision grammar, without treating wire objects as authority.
            try { FeatureWireParser.ParseRequiredHeader([id + "=" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)]); }
            catch (FeatureFormatException) { throw Invalid(); }
            if (!seen.Add(id) || !offer.Features.TryGetValue(id, out var revisions)
                || !revisions.Any(r => r.Revision == revision) || !locallyEnabled
                || id != Selected.Id || revision != Selected.Revision) throw Invalid();
            selections.Add(new(id, revision));
        }
        if (offer.Required.Any(id => !seen.Contains(id))) throw Unsupported();
        return selections.AsReadOnly();
    }

    internal static JsonElement SerializeSelection(IReadOnlyList<FeatureSelection> selected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in selected)
            {
                writer.WriteStartObject(); writer.WriteString("id", item.Id);
                writer.WriteNumber("revision", item.Revision); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    internal static void ValidateRawHello(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        if (!root.EnumerateObject().Any(p => OfferKeys.Any(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase)))) return;
        if (payload.Length > 16384) throw Invalid();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw Invalid();
            if (OfferKeys.Any(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase))
                && !OfferKeys.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
                if (OfferKeys.Contains(property.Name, StringComparer.Ordinal)) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        FeatureWireParser.ParseOffer(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static string OfferJson(HelloFrame hello)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (hello.FeatureNegotiation is { } version) writer.WriteNumber("feature_negotiation", version);
            if (hello.FeatureOffer is { } offer) { writer.WritePropertyName("feature_offer"); offer.WriteTo(writer); }
            if (hello.RequiredFeatures is { } required)
            {
                writer.WriteStartArray("required_features");
                foreach (var id in required) writer.WriteStringValue(id);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static NcpHandshakeException Invalid() => new(NcpErrorCodes.FeatureNegotiationInvalid, "Native feature negotiation rejected.");
    private static NcpHandshakeException Unsupported() => new(NcpErrorCodes.FeatureRequiredUnsupported, "Required native feature unavailable.");
}
