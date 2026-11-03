// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text;
using System.Globalization;
using NPS.Core.Exceptions;
using NPS.Core.Frames;

namespace NPS.Core.Ncp;

internal static class SchemaBatchValues
{
    internal static string CheckSchema(FrameSchema schema, uint ttl)
    {
        if (schema?.Fields is not { Count: >= 1 and <= 64 } fields || ttl is 0 or > 900) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
            if (field is null || string.IsNullOrEmpty(field.Name) || Encoding.UTF8.GetByteCount(field.Name) > 64
                || !names.Add(field.Name) || field.Type is not ("string" or "bool" or "int32" or "uint64")) throw Invalid();
        return SchemaBatchFrameCodec.ComputeAnchorId(schema);
    }

    internal static bool Index(string text, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 10 && (text.Length == 1 || text[0] != '0')
            && text.All(c => c is >= '0' and <= '9')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    internal static void CheckRow(FrameSchema schema, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != schema.Fields.Count) throw Invalid();
        var index = 0;
        foreach (var value in row.EnumerateArray()) CheckValue(value, schema.Fields[index++]);
    }

    internal static void CheckValue(JsonElement value, SchemaField field)
    {
        if (value.ValueKind == JsonValueKind.Null && field.Nullable) return;
        var integer = value.ValueKind == JsonValueKind.Number && !value.GetRawText().AsSpan().ContainsAny('.', 'e', 'E');
        var valid = field.Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "int32" => integer && value.TryGetInt32(out _),
            "uint64" => integer && value.TryGetUInt64(out var number) && number <= 9007199254740991,
            _ => false,
        };
        if (!valid) throw Invalid();
    }

    private static NpsException Invalid() => new("Invalid schema-batch value.", NpsStatusCodes.ClientBadFrame, NcpErrorCodes.AnchorSchemaInvalid);
}
