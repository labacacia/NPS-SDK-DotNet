// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.NIP.Invocation;

namespace NPS.Tests.Alpha20;

public sealed class NipInvocationJsonTests
{
    public static IEnumerable<object[]> SharedVectors()
    {
        foreach (var vector in ReadCorpus().GetProperty("vectors").EnumerateArray())
            yield return [vector.GetProperty("id").GetString()!, vector.Clone()];
    }

    public static JsonElement ReadCorpus()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "spec/conformance/alpha20/context_crypto_vectors.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        throw new FileNotFoundException("Shared context byte fixtures are required.");
    }

    [Theory]
    [MemberData(nameof(SharedVectors))]
    public void SharedJcsDigestBytesOrExplicitUnsupportedNumericSchema(string id, JsonElement vector)
    {
        var request = vector.GetProperty("request");
        if (id == "numbers")
        {
            // Fractional/exponent request schemas remain UNSUPPORTED; this is not generic JCS qualification.
            Assert.Throws<NipInvocationException>(() => NipInvocationJson.RequestDigest(request));
            return;
        }
        Assert.Equal(vector.GetProperty("request_jcs").GetString(), Encoding.UTF8.GetString(NipInvocationJson.Canonicalize(request)));
        Assert.Equal(vector.GetProperty("request_digest").GetString(), NipInvocationJson.RequestDigest(request));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"text\":\"\\ud800\"}")]
    [InlineData("{\"text\":\"\\udc00\"}")]
    [InlineData("{\"n\":9007199254740992}")]
    [InlineData("{\"n\":9007199254740991.1}")]
    [InlineData("{\"n\":1.0}")]
    [InlineData("{\"n\":1e0}")]
    [InlineData("{\"n\":1e309}")]
    [InlineData("{} {}")]
    [InlineData("\ufeff{}")]
    public void UnsupportedOrAmbiguousInputRejectsWithoutRoundedHandlerValues(string raw)
    {
        var error = Assert.Throws<NipInvocationException>(() => NipInvocationJson.Parse(raw, 16384));
        Assert.Equal("NIP-CONTEXT-INVALID", error.Code);
    }

    [Fact]
    public void StringsPreserveUnicodeAndUseLowercaseJcsControlEscapes()
    {
        var value = NipInvocationJson.Parse("{\"a\":\"é e\\u0301 \\ud83d\\ude00 \\u000f / \\n\",\"zero\":-0}", 16384);
        Assert.Equal("{\"a\":\"é é 😀 \\u000f / \\n\",\"zero\":0}", Encoding.UTF8.GetString(NipInvocationJson.Canonicalize(value)));
    }

    [Fact]
    public void CompleteBodyPreservesPresenceAndBindsExecutionOptionsButNotCorrelation()
    {
        FeatureSelection[] features = [new("nps.data_results", 1), new("nps.invocation_context", 1)];
        const string raw = """{"action_id":"result.read","params":{"result_ref":"r","limit":1},"request_id":"trace-a","priority":"normal","async":false,"timeout_ms":5000}""";
        var original = NipInvocationJson.ResultActionRequest(raw, "service:results", features);
        var traceChanged = NipInvocationJson.ResultActionRequest(raw.Replace("trace-a", "trace-b"), "service:results", features);
        Assert.Equal(NipInvocationJson.RequestDigest(original), NipInvocationJson.RequestDigest(traceChanged));
        var priorityChanged = NipInvocationJson.ResultActionRequest(raw.Replace("normal", "high"), "service:results", features);
        Assert.NotEqual(NipInvocationJson.RequestDigest(original), NipInvocationJson.RequestDigest(priorityChanged));
        var absentDefault = NipInvocationJson.ResultActionRequest(raw.Replace(",\"timeout_ms\":5000", ""), "service:results", features);
        Assert.NotEqual(NipInvocationJson.RequestDigest(original), NipInvocationJson.RequestDigest(absentDefault));
        Assert.False(original.GetProperty("body").GetProperty("value").TryGetProperty("request_id", out _));
    }

    [Fact]
    public void ByteAndDepthBoundsRejectBeforeCanonicalizationAdmission()
    {
        Assert.Throws<NipInvocationException>(() => NipInvocationJson.Parse("{}" + new string(' ', 16383), 16384));
        Assert.Throws<NipInvocationException>(() => NipInvocationJson.Parse(new string('[', 66) + new string(']', 66), 16384));
    }
}
