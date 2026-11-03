// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPS.Core.Alpha20;

/// <summary>Experimental CR-0012 revision descriptor; not an authorization grant.</summary>
public sealed record FeatureRevision(ushort Revision, string Lifecycle);

/// <summary>A validated exact feature requirement.</summary>
public sealed record FeatureSelection(string Id, ushort Revision);

/// <summary>Strict parse error from experimental CR-0012 helpers.</summary>
public sealed class FeatureFormatException(string code) : FormatException(code)
{
    /// <summary>Proposed CR-0012 error code.</summary>
    public string Code { get; } = code;
}

/// <summary>Validated offer; this is not an admitted session or selected feature set.</summary>
public sealed record FeatureOffer(
    IReadOnlyDictionary<string, IReadOnlyList<FeatureRevision>> Features,
    IReadOnlyList<string> Required);

/// <summary>
/// Experimental bounded wire parsers. Does not advertise support, negotiate a session,
/// authorize requests, or attach headers to existing clients.
/// </summary>
public static class FeatureWireParser
{
    private const string NativeInvalid = "NCP-FEATURE-NEGOTIATION-INVALID";
    private const string HttpInvalid = "NWP-FEATURE-REQUIREMENTS-INVALID";
    private static readonly Regex IdPattern = new(
        @"\A[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Parse a standalone Hello feature block with duplicate-key rejection.</summary>
    public static FeatureOffer ParseOffer(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        try
        {
            Need(StrictUtf8.GetByteCount(raw) <= 16384);
            using var document = JsonDocument.Parse(raw);
            var block = Members(document.RootElement,
                ["feature_negotiation", "feature_offer", "required_features"]);
            var features = new Dictionary<string, IReadOnlyList<FeatureRevision>>(StringComparer.Ordinal);
            var required = new List<string>();
            if (block.Count == 0)
                return Result(features, required);
            Need(block.TryGetValue("feature_negotiation", out var version)
                && Integer(version) == 1);
            if (block.TryGetValue("feature_offer", out var offers))
            {
                Need(offers.ValueKind == JsonValueKind.Array && offers.GetArrayLength() <= 32);
                foreach (var item in offers.EnumerateArray())
                {
                    var fields = Members(item, ["id", "revisions"]);
                    Need(fields.Count == 2 && fields.ContainsKey("id") && fields.ContainsKey("revisions"));
                    var id = Name(fields["id"]);
                    Need(!features.ContainsKey(id));
                    var revisions = fields["revisions"];
                    Need(revisions.ValueKind == JsonValueKind.Array
                        && revisions.GetArrayLength() is >= 1 and <= 16);
                    var values = new List<FeatureRevision>();
                    ushort last = 0;
                    foreach (var entry in revisions.EnumerateArray())
                    {
                        var descriptor = Members(entry, ["revision", "lifecycle"]);
                        Need(descriptor.Count == 2 && descriptor.ContainsKey("revision")
                            && descriptor.ContainsKey("lifecycle"));
                        var number = Integer(descriptor["revision"]);
                        Need(number > last);
                        var lifecycle = descriptor["lifecycle"];
                        Need(lifecycle.ValueKind == JsonValueKind.String);
                        var label = lifecycle.GetString()!;
                        Need(label is "experimental" or "stable" or "deprecated");
                        values.Add(new FeatureRevision(number, label));
                        last = number;
                    }
                    features.Add(id, values.AsReadOnly());
                }
            }
            if (block.TryGetValue("required_features", out var requirements))
            {
                Need(requirements.ValueKind == JsonValueKind.Array && requirements.GetArrayLength() <= 32);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in requirements.EnumerateArray())
                {
                    var name = Name(item);
                    Need(features.ContainsKey(name) && seen.Add(name));
                    required.Add(name);
                }
            }
            return Result(features, required);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException
                                   or EncoderFallbackException or FormatException)
        {
            throw new FeatureFormatException(NativeInvalid);
        }
    }

    /// <summary>
    /// Parse raw header instances before a framework joins them. Omission is empty;
    /// an empty present header or repeated instance is invalid.
    /// </summary>
    public static IReadOnlyList<FeatureSelection> ParseRequiredHeader(IReadOnlyList<string> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        try
        {
            Need(instances.Count <= 1);
            if (instances.Count == 0) return Array.Empty<FeatureSelection>();
            var raw = instances[0];
            Need(raw is not null && raw.Length <= 4096 && raw.All(c => c <= 127));
            var pairs = raw!.Split(',');
            Need(pairs.Length is >= 1 and <= 32);
            var selected = new List<FeatureSelection>();
            var previous = "";
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=');
                Need(parts.Length == 2);
                var id = parts[0];
                Need(ValidId(id) && StringComparer.Ordinal.Compare(id, previous) > 0);
                var digits = parts[1];
                Need(digits.Length is >= 1 and <= 5 && digits[0] != '0'
                    && digits.All(c => c is >= '0' and <= '9'));
                Need(ushort.TryParse(digits, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0);
                selected.Add(new FeatureSelection(id, number));
                previous = id;
            }
            return selected.AsReadOnly();
        }
        catch (FormatException)
        {
            throw new FeatureFormatException(HttpInvalid);
        }
    }

    private static FeatureOffer Result(Dictionary<string, IReadOnlyList<FeatureRevision>> features,
        List<string> required) => new(
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<FeatureRevision>>(features),
            required.AsReadOnly());

    private static Dictionary<string, JsonElement> Members(JsonElement value, string[] allowed)
    {
        Need(value.ValueKind == JsonValueKind.Object);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            Need(allowed.Contains(field.Name, StringComparer.Ordinal)
                && fields.TryAdd(field.Name, field.Value));
        return fields;
    }

    private static string Name(JsonElement value)
    {
        Need(value.ValueKind == JsonValueKind.String);
        var name = value.GetString()!;
        Need(ValidId(name));
        return name;
    }

    private static bool ValidId(string value) => value.Length <= 96 && IdPattern.IsMatch(value);

    private static ushort Integer(JsonElement value)
    {
        Need(value.ValueKind == JsonValueKind.Number);
        var raw = value.GetRawText();
        Need(raw.Length is >= 1 and <= 5 && raw.All(c => c is >= '0' and <= '9'));
        Need(value.TryGetUInt16(out var number) && number > 0);
        return number;
    }

    private static void Need(bool condition)
    {
        if (!condition) throw new FormatException();
    }
}
