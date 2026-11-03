using System.Text.Json;
using NPS.Core.Alpha20;

namespace NPS.Tests.Alpha20;

public sealed class FeatureWireParserTests
{
    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName,
                "spec/conformance/alpha20/feature_negotiation_vectors.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
                    yield return [item.GetProperty("id").GetString()!, item.Clone()];
                yield break;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Shared alpha20 feature fixtures are required.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedParserFixtures(string id, JsonElement vector)
    {
        Assert.NotEmpty(id);
        var expected = vector.GetProperty("expected");
        var parserError = expected.TryGetProperty("error", out var error)
            && error.GetString() is "NCP-FEATURE-NEGOTIATION-INVALID"
                or "NWP-FEATURE-REQUIREMENTS-INVALID";
        if (vector.GetProperty("kind").GetString() == "native-offer")
        {
            var raw = vector.GetProperty("raw").GetString()!;
            if (parserError)
                Assert.Equal(error.GetString(), Assert.Throws<FeatureFormatException>(
                    () => FeatureWireParser.ParseOffer(raw)).Code);
            else
            {
                // Support/policy selection is deliberately not a parser responsibility.
                var offer = FeatureWireParser.ParseOffer(raw);
                Assert.All(offer.Required, name => Assert.True(offer.Features.ContainsKey(name)));
                using var input = JsonDocument.Parse(raw);
                var root = input.RootElement;
                var entries = root.TryGetProperty("feature_offer", out var items)
                    ? items.EnumerateArray().ToArray() : [];
                Assert.Equal(entries.Length, offer.Features.Count);
                foreach (var entry in entries)
                {
                    var parsed = offer.Features[entry.GetProperty("id").GetString()!];
                    var revisions = entry.GetProperty("revisions").EnumerateArray().ToArray();
                    Assert.Equal(revisions.Length, parsed.Count);
                    for (var index = 0; index < revisions.Length; index++)
                    {
                        Assert.Equal(revisions[index].GetProperty("revision").GetUInt16(), parsed[index].Revision);
                        Assert.Equal(revisions[index].GetProperty("lifecycle").GetString(), parsed[index].Lifecycle);
                    }
                }
                var names = root.TryGetProperty("required_features", out var requiredNames)
                    ? requiredNames.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
                Assert.Equal(names, offer.Required);
            }
        }
        else
        {
            var headers = vector.GetProperty("headers").EnumerateArray()
                .Select(x => x.GetString()!).ToArray();
            if (parserError)
                Assert.Equal(error.GetString(), Assert.Throws<FeatureFormatException>(
                    () => FeatureWireParser.ParseRequiredHeader(headers)).Code);
            else
            {
                var parsed = FeatureWireParser.ParseRequiredHeader(headers);
                var required = expected.GetProperty("required").EnumerateArray().ToArray();
                Assert.Equal(required.Length, parsed.Count);
                for (var index = 0; index < required.Length; index++)
                {
                    Assert.Equal(required[index].GetProperty("id").GetString(), parsed[index].Id);
                    Assert.Equal(required[index].GetProperty("revision").GetUInt16(), parsed[index].Revision);
                }
            }
        }
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("-1")]
    [InlineData("null")]
    public void RejectsNonIntegerRevisionSyntax(string revision)
    {
        var raw = "{\"feature_negotiation\":1,\"feature_offer\":[{\"id\":\"example.echo\","
            + "\"revisions\":[{\"revision\":" + revision + ",\"lifecycle\":\"stable\"}]}]}";
        Assert.Throws<FeatureFormatException>(() => FeatureWireParser.ParseOffer(raw));
    }
}
