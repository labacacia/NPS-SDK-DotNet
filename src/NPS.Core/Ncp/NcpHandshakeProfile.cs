// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>
/// Server-side capabilities used for deterministic native NCP negotiation
/// (NPS-1 §2.6.2).
/// </summary>
public sealed class NcpHandshakeProfile
{
    /// <summary>
    /// Explicit experimental CR-0017 opt-in. Only enable after this host's actual
    /// producer/source/binding qualification; default false and not inferred from version.
    /// </summary>
    public bool SchemaBatchesEnabled { get; init; }
    public string MinVersion { get; init; } = "0.1";
    public string NpsVersion { get; init; } = "0.11";
    public IReadOnlyList<string> SupportedEncodings { get; init; } =
        ["msgpack", "json", "binary_vector.v1"];
    public IReadOnlyList<string> SupportedProtocols { get; init; } =
        ["ncp", "nwp", "nip", "ndp", "nop"];
    public uint MaxFramePayload { get; init; } = FrameHeader.DefaultMaxPayload;
    public bool ExtSupport { get; init; }
    public uint MaxConcurrentStreams { get; init; } = 32;
}

/// <summary>Observable outcome of one native-mode handshake stage.</summary>
public enum NcpHandshakeAction
{
    Continue,
    Accept,
    SilentClose,
    ErrorClose,
}

/// <summary>
/// Portable handshake decision shared by admission checks and negotiation.
/// </summary>
public sealed record NcpHandshakeDecision
{
    public required NcpHandshakeAction Action { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? DiagnosticError { get; init; }
    public string? SessionVersion { get; init; }
    public string? NegotiatedEncoding { get; init; }
    public IReadOnlyList<string>? EnabledEncodings { get; init; }
    public IReadOnlyList<string>? SupportedProtocols { get; init; }
    public uint? MaxFramePayload { get; init; }
    public bool? ExtSupport { get; init; }
    public uint? MaxConcurrentStreams { get; init; }
}

/// <summary>
/// Pure policy functions for the NCP v0.11 native server profile.
/// </summary>
public static class NcpNativeServerPolicy
{
    public static NcpHandshakeDecision EvaluatePreamble(
        ReadOnlySpan<byte> received,
        TimeSpan elapsed,
        TimeSpan timeout)
    {
        if (timeout > TimeSpan.Zero && elapsed >= timeout)
            return SilentClose();

        if (received.Length < NcpPreamble.Length)
            return Continue();

        if (!NcpPreamble.Matches(received))
            return SilentClose(NcpErrorCodes.PreambleInvalid);

        return Continue();
    }

    public static NcpHandshakeDecision EvaluateHelloHeader(
        FrameHeader header,
        TimeSpan elapsed,
        TimeSpan timeout,
        uint maxHelloPayload)
    {
        if (timeout > TimeSpan.Zero && elapsed >= timeout)
            return SilentClose();

        if (header.FrameType != FrameType.Hello
            || header.EncodingTier != EncodingTier.Json
            || header.IsEncrypted
            || header.IsExtended
            || header.PayloadLength > maxHelloPayload)
        {
            return SilentClose();
        }

        return Continue();
    }

    public static NcpHandshakeDecision Negotiate(
        NcpHandshakeProfile server,
        HelloFrame client)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);

        if (!TryParseVersion(server.MinVersion, out var serverMin)
            || !TryParseVersion(server.NpsVersion, out var serverMax)
            || !TryParseVersion(client.MinVersion ?? client.NpsVersion, out var clientMin)
            || !TryParseVersion(client.NpsVersion, out var clientMax)
            || serverMin.CompareTo(serverMax) > 0
            || clientMin.CompareTo(clientMax) > 0)
        {
            return ErrorClose(
                NpsStatusCodes.ProtoVersionIncompatible,
                NcpErrorCodes.VersionIncompatible);
        }

        var overlapMin = ProtocolVersion.Max(serverMin, clientMin);
        var overlapMax = ProtocolVersion.Min(serverMax, clientMax);
        if (overlapMin.CompareTo(overlapMax) > 0)
        {
            return ErrorClose(
                NpsStatusCodes.ProtoVersionIncompatible,
                NcpErrorCodes.VersionIncompatible);
        }

        var serverEncodings = server.SupportedEncodings.ToHashSet(StringComparer.Ordinal);
        var stableEncoding = client.SupportedEncodings.FirstOrDefault(
            token => token is "msgpack" or "json" && serverEncodings.Contains(token));
        if (stableEncoding is null)
        {
            return ErrorClose(
                NpsStatusCodes.ServerEncodingUnsupported,
                NcpErrorCodes.EncodingUnsupported);
        }

        var serverProtocols = server.SupportedProtocols.ToHashSet(StringComparer.Ordinal);
        var protocols = client.SupportedProtocols
            .Where(serverProtocols.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!protocols.Contains("ncp", StringComparer.Ordinal)
            || client.MaxFramePayload == 0
            || server.MaxFramePayload == 0
            || client.MaxConcurrentStreams == 0
            || server.MaxConcurrentStreams == 0)
        {
            return ErrorClose(
                NpsStatusCodes.ProtoVersionIncompatible,
                NcpErrorCodes.VersionIncompatible);
        }

        var enabledEncodings = new List<string> { stableEncoding };
        if (serverEncodings.Contains("binary_vector.v1")
            && client.SupportedEncodings.Contains("binary_vector.v1", StringComparer.Ordinal))
        {
            enabledEncodings.Add("binary_vector.v1");
        }

        return new NcpHandshakeDecision
        {
            Action = NcpHandshakeAction.Accept,
            SessionVersion = overlapMax.ToString(),
            NegotiatedEncoding = stableEncoding,
            EnabledEncodings = enabledEncodings,
            SupportedProtocols = protocols,
            MaxFramePayload = Math.Min(server.MaxFramePayload, client.MaxFramePayload),
            ExtSupport = server.ExtSupport && client.ExtSupport,
            MaxConcurrentStreams = Math.Min(
                server.MaxConcurrentStreams,
                client.MaxConcurrentStreams),
        };
    }

    private static NcpHandshakeDecision SilentClose(string? diagnosticError = null) =>
        new()
        {
            Action = NcpHandshakeAction.SilentClose,
            DiagnosticError = diagnosticError,
        };

    private static NcpHandshakeDecision Continue() =>
        new()
        {
            Action = NcpHandshakeAction.Continue,
        };

    private static NcpHandshakeDecision ErrorClose(string status, string error) =>
        new()
        {
            Action = NcpHandshakeAction.ErrorClose,
            Status = status,
            Error = error,
        };

    private static bool TryParseVersion(string value, out ProtocolVersion version)
    {
        version = default;
        var parts = value.Split('.');
        if (parts.Length != 2
            || !uint.TryParse(parts[0], out var major)
            || !uint.TryParse(parts[1], out var minor))
        {
            return false;
        }

        version = new ProtocolVersion(major, minor);
        return true;
    }

    private readonly record struct ProtocolVersion(uint Major, uint Minor) :
        IComparable<ProtocolVersion>
    {
        public int CompareTo(ProtocolVersion other)
        {
            var major = Major.CompareTo(other.Major);
            return major != 0 ? major : Minor.CompareTo(other.Minor);
        }

        public static ProtocolVersion Min(ProtocolVersion left, ProtocolVersion right) =>
            left.CompareTo(right) <= 0 ? left : right;

        public static ProtocolVersion Max(ProtocolVersion left, ProtocolVersion right) =>
            left.CompareTo(right) >= 0 ? left : right;

        public override string ToString() => $"{Major}.{Minor}";
    }
}
