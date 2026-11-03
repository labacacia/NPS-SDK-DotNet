// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

// Profile-only preservation of wire presence. Typed init-only DTO decoding can
// overwrite an omitted default and conflate nullable Value's missing/null states.
// Legacy codec/DTO behavior is left unchanged.
internal static class SchemaBatchDiffWireFields
{
    internal static DiffFrame Restore(DiffFrame frame, JsonElement body)
    {
        var result = Member(body, "patch_format", out _) ? frame : frame with { PatchFormat = NcpPatchFormat.JsonPatch };
        if (frame.Patch is not { Count: <= 256 } patches || !Member(body, "patch", out var raw)
            || raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() != patches.Count) return result;
        var preserved = patches.ToArray();
        for (var index = 0; index < patches.Count; index++)
            if (patches[index] is { Value: null } operation && Member(raw[index], "value", out var value)
                && value.ValueKind == JsonValueKind.Null)
                preserved[index] = operation with { Value = value.Clone() };
        return result with { Patch = Array.AsReadOnly(preserved) };
    }

    private static bool Member(JsonElement value, string name, out JsonElement member)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                { member = property.Value; return true; }
        member = default;
        return false;
    }
}
