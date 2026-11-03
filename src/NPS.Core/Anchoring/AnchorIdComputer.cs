// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Core.Frames;

namespace NPS.Core.Anchoring;

/// <summary>
/// Computes the <c>anchor_id</c> for a <see cref="FrameSchema"/> per NPS-1 §4.1.
///
/// <para>
/// Algorithm:
/// <list type="number">
///   <item>Serialise the schema to RFC 8785 JCS (JSON Canonicalization Scheme) — object keys
///         sorted by UTF-16 code-unit order, no extra whitespace.</item>
///   <item>Compute SHA-256 of the UTF-8 representation of the canonical JSON.</item>
///   <item>Format as <c>sha256:{64 lower-hex chars}</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>JCS implementation note:</b> No suitable RFC 8785 NuGet package was available at the time
/// of writing. This class provides a <em>targeted</em> JCS implementation for <see cref="FrameSchema"/>
/// only — the structure is fixed and fully deterministic. When a cross-platform JCS library becomes
/// available on NuGet it should replace this implementation to guarantee cross-SDK alignment.
/// JCS key order for <see cref="SchemaField"/>: <c>name &lt; nullable &lt; semantic &lt; type</c>
/// (ASCII byte order = UTF-16 code-unit order for these ASCII-only keys).
/// </para>
/// </summary>
/// <remarks>
/// Unselected alpha.19 escaping is retained for compatibility. The optional schema-batch
/// profile uses SchemaBatchFrameCodec.ComputeAnchorId for strict Unicode/HTML JCS strings.
/// The historical public helper is not a general arbitrary-JSON canonicalizer.
/// </remarks>
public static class AnchorIdComputer
{
    /// <summary>
    /// Computes the canonical <c>anchor_id</c> for <paramref name="schema"/>.
    /// </summary>
    /// <returns><c>sha256:{64-char lowercase hex}</c></returns>
    public static string Compute(FrameSchema schema)
    {
        var canonical = CanonicalJson(schema);
        var bytes     = Encoding.UTF8.GetBytes(canonical);
        var hash      = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the RFC 8785 JCS canonical JSON string for <paramref name="schema"/>.
    /// Useful for debugging and cross-SDK verification.
    /// </summary>
    public static string CanonicalJson(FrameSchema schema) => CanonicalJson(schema, AppendJcsString);

    // Optional profiles can supply the strict shared string primitive without
    // changing the unselected alpha.19 helper's historical escaping behavior.
    internal static string CanonicalJson(FrameSchema schema, Action<StringBuilder, string> appendString)
    {
        var sb = new StringBuilder();

        // Top-level object has a single key "fields" — no sort needed.
        sb.Append("{\"fields\":[");

        for (int i = 0; i < schema.Fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendField(sb, schema.Fields[i], appendString);
        }

        sb.Append("]}");
        return sb.ToString();
    }

    // JCS key order for SchemaField (UTF-16 code-unit / ASCII byte order):
    //   "name"     (n=110, a=97 …)
    //   "nullable" (n=110, u=117 …)   — 'u' > 'a', so "nullable" > "name"
    //   "semantic" (s=115 …)          — 's' > 'n'
    //   "type"     (t=116 …)          — 't' > 's'
    // "semantic" is omitted when null (matches WhenWritingNull serialiser behaviour).
    private static void AppendField(StringBuilder sb, SchemaField field, Action<StringBuilder, string> appendString)
    {
        sb.Append('{');

        sb.Append("\"name\":");
        appendString(sb, field.Name);

        sb.Append(",\"nullable\":");
        sb.Append(field.Nullable ? "true" : "false");

        if (field.Semantic is not null)
        {
            sb.Append(",\"semantic\":");
            appendString(sb, field.Semantic);
        }

        sb.Append(",\"type\":");
        appendString(sb, field.Type);

        sb.Append('}');
    }

    /// <summary>
    /// Appends a JCS-compliant JSON string literal.
    /// Delegates escaping to <see cref="JsonSerializer"/> which follows the JSON spec
    /// (RFC 8259) — sufficient for our ASCII-domain schema identifiers.
    /// </summary>
    private static void AppendJcsString(StringBuilder sb, string value) =>
        sb.Append(JsonSerializer.Serialize(value));
}
