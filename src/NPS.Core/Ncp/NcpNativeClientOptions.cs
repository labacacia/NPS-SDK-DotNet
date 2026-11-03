// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;

namespace NPS.Core.Ncp;

/// <summary>Explicit client transport/experimental policy; no identity or source grant is inferred.</summary>
public sealed class NcpNativeClientOptions
{
    /// <summary>Local CR-0017 opt-in, required in addition to offering and selecting the exact revision.</summary>
    public bool SchemaBatchesEnabled { get; init; }

    /// <summary>Host-owned TLS/pin/identity verification before sending any NCP bytes.</summary>
    public Func<TcpClient, Stream, CancellationToken, ValueTask<Stream>>? AuthenticateStreamAsync { get; init; }

    /// <summary>Require a host authentication hook returning a distinct verified transport wrapper.</summary>
    public bool RequireAuthenticatedStream { get; init; }
}
