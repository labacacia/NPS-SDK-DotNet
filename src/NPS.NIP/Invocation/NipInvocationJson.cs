// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.Core.Serialization;

namespace NPS.NIP.Invocation;

/// <summary>Bounded context error category. Messages never include the signed envelope or request.</summary>
public sealed class NipInvocationException(string code) : Exception(code)
{
    /// <summary>Registered NIP-CONTEXT error code suitable for the existing ErrorFrame mapping.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// JCS for the explicitly restricted reference result-operation schema: strings, booleans,
/// null, arrays/objects and decimal integer tokens in the binary64 safe range. Fractions,
/// exponent tokens and unsafe integers reject before replay admission. This is NOT a general
/// JCS implementation or a claim of arbitrary NWP invocation-context qualification.
/// Valid inputs use exact RFC 8785 serialization, not IdentFrame signing canonicalization.
/// </summary>
public static class NipInvocationJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Strictly parse the integer-only reference schema, preserving original field presence.</summary>
    public static JsonElement Parse(string raw, int maximumBytes)
    {
        try
        {
            if (maximumBytes is < 1 or > 1_048_576 || Utf8.GetByteCount(raw) > maximumBytes) throw Invalid();
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
            var canonical = Canonicalize(document.RootElement);
            if (canonical.Length > maximumBytes) throw Invalid();
            return document.RootElement.Clone();
        }
        catch (NipInvocationException) { throw; }
        catch (Exception error) when (error is JsonException or EncoderFallbackException
                                      or InvalidOperationException or ArgumentException or FormatException)
        { throw Invalid(); }
    }

    /// <summary>
    /// Serialize the supported schema to exact JCS UTF-8, rejecting duplicate decoded keys,
    /// invalid Unicode and unsupported numeric values. Object ordering is ordinal UTF-16;
    /// scope/capability array ordering is checked separately by the verifier.
    /// </summary>
    public static byte[] Canonicalize(JsonElement value)
    {
        try
        {
            var builder = new StringBuilder();
            Append(value, builder, 0);
            return Utf8.GetBytes(builder.ToString());
        }
        catch (NipInvocationException) { throw; }
        catch (Exception error) when (error is EncoderFallbackException or InvalidOperationException
                                      or ArgumentException or FormatException)
        { throw Invalid(); }
    }

    /// <summary>Domain-separated request digest under NIP §7.8.3 for the supported schema.</summary>
    public static string RequestDigest(JsonElement logicalRequest)
    {
        var canonical = Canonicalize(logicalRequest);
        if (canonical.Length > 1_048_576) throw Invalid();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("NPS-INVOKE-REQUEST/1\0"u8);
        hash.AppendData(canonical);
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Build the complete NWP §9.7 logical ActionFrame request from original JSON, never a DTO
    /// with inserted defaults. Target and selected pairs come from authenticated host routing.
    /// Only the four registered result operations and the integer-only reference schema are supported.
    /// </summary>
    public static JsonElement ResultActionRequest(string rawFrame, string target,
        IReadOnlyList<FeatureSelection> requiredFeatures)
    {
        var frame = Parse(rawFrame, 65536);
        if (frame.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(target)
            || Utf8.GetByteCount(target) > 512 || requiredFeatures.Count > 32) throw Invalid();
        if (frame.TryGetProperty("invocation_context", out _) || frame.TryGetProperty("required_features", out _)) throw Invalid();
        if (frame.TryGetProperty("frame", out var marker)
            && !(marker.ValueKind == JsonValueKind.String && marker.GetString() == "0x11"
                || marker.ValueKind == JsonValueKind.Number && marker.TryGetUInt32(out var number) && number == 0x11)) throw Invalid();
        if (!frame.TryGetProperty("action_id", out var action) || action.ValueKind != JsonValueKind.String
            || action.GetString() is not ("result.create" or "result.read" or "result.aggregate" or "result.release")) throw Invalid();
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in frame.EnumerateObject())
            if (property.Name is not ("frame" or "request_id" or "action_id" or "idempotency_key"))
                body.Add(property.Name, property.Value);
        JsonElement? key = frame.TryGetProperty("idempotency_key", out var idempotency) ? idempotency : null;
        if (key is { ValueKind: not (JsonValueKind.String or JsonValueKind.Null) }) throw Invalid();
        return JsonSerializer.SerializeToElement(new
        {
            protocol = "nwp", operation = action.GetString(), target,
            idempotency_key = key, required_features = requiredFeatures.Select(pair => new { id = pair.Id, revision = pair.Revision }),
            body = new { encoding = "json", value = body },
        });
    }

    private static void Append(JsonElement value, StringBuilder output, int depth)
    {
        if (depth > 64) throw Invalid();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var fields = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                if (fields.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) throw Invalid();
                output.Append('{');
                for (var index = 0; index < fields.Length; index++)
                {
                    if (index != 0) output.Append(',');
                    AppendString(fields[index].Name, output);
                    output.Append(':');
                    Append(fields[index].Value, output, depth + 1);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) output.Append(',');
                    first = false;
                    Append(item, output, depth + 1);
                }
                output.Append(']');
                break;
            case JsonValueKind.String: AppendString(value.GetString()!, output); break;
            case JsonValueKind.Number:
                var token = value.GetRawText();
                if (token.Any(character => character is not (>= '0' and <= '9') and not '-')
                    || !value.TryGetInt64(out var integer) || integer is < -9_007_199_254_740_991 or > 9_007_199_254_740_991)
                    throw Invalid();
                output.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw Invalid();
        }
    }

    private static void AppendString(string value, StringBuilder output)
    {
        JcsString.Append(output, value);
    }

    internal static NipInvocationException Invalid() => new("NIP-CONTEXT-INVALID");
}
