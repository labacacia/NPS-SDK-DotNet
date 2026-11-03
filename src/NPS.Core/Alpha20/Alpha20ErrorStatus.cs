using System.Collections.Frozen;

namespace NPS.Core.Alpha20;

/// <summary>Wire status only. Neither HTTP status nor this mapping grants retry permission.</summary>
public sealed record Alpha20ErrorMapping(string NpsStatus, int HttpStatus);

/// <summary>Canonical alpha.20 additions. Runtime routing/admission remains separate.</summary>
public static class Alpha20ErrorStatus
{
    /// <summary>Immutable mappings; unknown errors are not silently reclassified.</summary>
    public static IReadOnlyDictionary<string, Alpha20ErrorMapping> Mappings { get; } =
        new Dictionary<string, Alpha20ErrorMapping>(StringComparer.Ordinal)
        {
            ["NCP-FEATURE-NEGOTIATION-INVALID"] = new("NPS-CLIENT-BAD-FRAME", 400),
            ["NCP-FEATURE-REQUIRED-UNSUPPORTED"] = new("NPS-CLIENT-CONFLICT", 409),
            ["NWP-CHALLENGE-INVALID"] = new("NPS-CLIENT-BAD-FRAME", 400),
            ["NWP-CHALLENGE-NOT-FOUND"] = new("NPS-CLIENT-NOT-FOUND", 404),
            ["NWP-CHALLENGE-BINDING-CONFLICT"] = new("NPS-CLIENT-CONFLICT", 409),
            ["NWP-CHALLENGE-NOT-APPROVED"] = new("NPS-CLIENT-CONFLICT", 409),
            ["NWP-CHALLENGE-EXPIRED"] = new("NPS-CLIENT-CONFLICT", 409),
            ["NWP-CHALLENGE-STATE-UNAVAILABLE"] = new("NPS-SERVER-UNAVAILABLE", 503),
            ["NOP-EXECUTION-INDETERMINATE"] = new("NPS-SERVER-INTERNAL", 500),
            ["NWP-FEATURE-REQUIREMENTS-INVALID"] = new("NPS-CLIENT-BAD-FRAME", 400),
            ["NWP-FEATURE-REQUIRED-UNSUPPORTED"] = new("NPS-CLIENT-CONFLICT", 409),
            ["NIP-CONTEXT-INVALID"] = new("NPS-CLIENT-BAD-FRAME", 400),
            ["NIP-CONTEXT-UNTRUSTED"] = new("NPS-AUTH-FORBIDDEN", 403),
            ["NIP-CONTEXT-BINDING-MISMATCH"] = new("NPS-AUTH-FORBIDDEN", 403),
            ["NIP-CONTEXT-EXPIRED"] = new("NPS-AUTH-FORBIDDEN", 403),
            ["NIP-CONTEXT-REPLAY"] = new("NPS-CLIENT-CONFLICT", 409),
            ["NIP-CONTEXT-VERIFIER-UNAVAILABLE"] = new("NPS-SERVER-UNAVAILABLE", 503),
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
