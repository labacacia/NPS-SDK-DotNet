// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace NPS.Core.Serialization;

/// <summary>
/// RFC 8785 string primitive shared by the restricted invocation and schema profiles.
/// This is not a general JSON/numeric canonicalizer and performs no Unicode normalization.
/// </summary>
public static class JcsString
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Appends one quoted JCS string; invalid UTF-16 rejects before modifying output.</summary>
    public static void Append(StringBuilder output, string value)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);
        _ = Utf8.GetByteCount(value);
        output.Append('"');
        foreach (var character in value)
            output.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\t' => "\\t",
                '\n' => "\\n", '\f' => "\\f", '\r' => "\\r",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        output.Append('"');
    }
}
