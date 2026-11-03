// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    // Setup-only fixture fingerprint. Flush one row at a time so hashing an existing
    // source does not allocate a second whole-array UTF-16 string and UTF-8 byte array.
    private static string HashRuntimeFixtureRows(JsonElement rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new RuntimeHashSink(hash);
        using (var writer = new Utf8JsonWriter(sink))
        {
            writer.WriteStartArray();
            foreach (var row in rows.EnumerateArray()) { row.WriteTo(writer); writer.Flush(); }
            writer.WriteEndArray();
            writer.Flush();
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class RuntimeHashSink(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void RuntimeFixtureHashMatchesNormalizedUtf8IncludingEscapes()
    {
        var rows = JsonSerializer.SerializeToElement(new[] {
            new { id = "quote\"é<script>", amount_minor = 1234, refunded = true, note = (string?)null },
            new { id = "newline\n", amount_minor = -8, refunded = false, note = (string?)null },
        });
        var expected = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows)));
        Assert.Equal(expected, HashRuntimeFixtureRows(rows));
    }
}
