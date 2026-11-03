using System.Text.Json;
using NPS.Core;
using NPS.Core.Alpha20;
using NPS.NIP.Ca;
using NPS.NWP.Http;
using NPS.NOP;

namespace NPS.Tests.Alpha20;

public sealed class Alpha20ErrorStatusTests
{
    [Fact]
    public void SharedMappingsAndConstantsMatch()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string path;
        while (!File.Exists(path = Path.Combine(directory!.FullName, "spec/conformance/alpha20/error_status_vectors.json")))
            directory = directory.Parent ?? throw new FileNotFoundException("Required alpha20 fixture missing");
        using var corpus = JsonDocument.Parse(File.ReadAllText(path));
        var cases = corpus.RootElement.GetProperty("cases");
        Assert.Equal(17, cases.GetArrayLength());
        var types = new Dictionary<string, Type> { ["NCP"] = typeof(NcpErrorCodes), ["NWP"] = typeof(NwpErrorCodes), ["NIP"] = typeof(NipErrorCodes), ["NOP"] = typeof(NopErrorCodes) };
        foreach (var vector in cases.EnumerateArray())
        {
            var code = vector.GetProperty("code").GetString()!;
            Assert.Contains(types[code[..3]].GetFields(), field => field.IsLiteral && Equals(field.GetRawConstantValue(), code));
            Assert.True(Alpha20ErrorStatus.Mappings.TryGetValue(code, out var mapping));
            Assert.Equal(vector.GetProperty("status").GetString(), mapping.NpsStatus);
            Assert.Equal(vector.GetProperty("http").GetInt32(), mapping.HttpStatus);
        }
        Assert.False(Alpha20ErrorStatus.Mappings.ContainsKey("UNKNOWN"));
    }
}
