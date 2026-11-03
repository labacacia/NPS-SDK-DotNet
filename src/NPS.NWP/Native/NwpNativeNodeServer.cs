// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;

namespace NPS.NWP.Native;

/// <summary>
/// Host-independent native-mode NWP serving loop over an established NCP session.
/// Supports Memory Node <see cref="QueryFrame"/> and Action Node <see cref="ActionFrame"/>
/// dispatch without requiring ASP.NET middleware.
/// Actions pass provider authorization before execution. The host owns authenticated
/// transport and any claimed NIP/session binding; this server creates no identity proof.
/// </summary>
public sealed class NwpNativeNodeServer
{
    private readonly NpsFrameCodec _codec;
    private readonly NwpNativeNodeOptions _options;
    private readonly IMemoryNodeProvider? _memoryProvider;
    private readonly IActionNodeProvider? _actionProvider;

    public NwpNativeNodeServer(
        NpsFrameCodec codec,
        NwpNativeNodeOptions options,
        IMemoryNodeProvider? memoryProvider = null,
        IActionNodeProvider? actionProvider = null)
    {
        _codec = codec;
        _options = options;
        _memoryProvider = memoryProvider;
        _actionProvider = actionProvider;
    }

    /// <summary>Serves frames until EOF, cancellation, or a fatal write error.</summary>
    public async Task ServeAsync(NcpSession session, CancellationToken ct = default) =>
        await ServeAsync(session.GetStream(), session.EncodingPolicy, ct).ConfigureAwait(false);

    /// <summary>Serves frames from an already authenticated stream.</summary>
    public async Task ServeAsync(Stream stream, EncodingTier tier = EncodingTier.Json, CancellationToken ct = default)
        => await ServeAsync(stream, new NcpEncodingPolicy(tier), ct).ConfigureAwait(false);

    /// <summary>Serves frames from an already authenticated stream under a negotiated encoding policy.</summary>
    public async Task ServeAsync(Stream stream, NcpEncodingPolicy encodingPolicy, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            IFrame? response;
            try
            {
                var decoded = await ReadFrameAsync(stream, encodingPolicy, ct).ConfigureAwait(false);
                response = await DispatchAsync(decoded.Frame, decoded.PayloadLength, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (ActionNodeException ex)
            {
                response = new ErrorFrame { Status = ex.NpsStatus, Error = ex.ErrorCode, Message = ex.Message };
            }
            catch (NpsFrameException ex)
            {
                response = ToDecodeErrorFrame(ex, NcpErrorCodes.FrameFlagsInvalid);
            }
            catch (NpsCodecException ex)
            {
                response = ToDecodeErrorFrame(ex, NcpErrorCodes.BinaryVectorMalformed);
            }
            catch (NpsException ex) when (ex.NpsStatusCode is not null || ex.ProtocolErrorCode is not null)
            {
                response = ToErrorFrame(ex);
            }
            catch (Exception ex) when (ex is NpsException or JsonException or InvalidOperationException)
            {
                response = new ErrorFrame
                {
                    Status = NpsStatusCodes.ServerInternal,
                    Error = "NWP-NATIVE-DISPATCH-FAILED",
                    Message = ex.Message,
                };
            }

            if (response is not null)
                await WriteFrameAsync(stream, response, encodingPolicy.DefaultTier, ct).ConfigureAwait(false);
        }
    }

    public async Task<IFrame> DispatchAsync(IFrame frame, CancellationToken ct = default) =>
        await DispatchAsync(frame, 0, ct).ConfigureAwait(false);

    private async Task<IFrame> DispatchAsync(IFrame frame, ulong wireInputBytes, CancellationToken ct) =>
        frame switch
        {
            QueryFrame query => await DispatchQueryAsync(query, ct).ConfigureAwait(false),
            ActionFrame action => await DispatchActionAsync(action, wireInputBytes, ct).ConfigureAwait(false),
            _ => new ErrorFrame
            {
                Status = NpsStatusCodes.ClientBadFrame,
                Error = "NWP-NATIVE-FRAME-UNSUPPORTED",
                Message = $"Native NWP server does not handle frame type {frame.FrameType}.",
            },
        };

    private async Task<CapsFrame> DispatchQueryAsync(QueryFrame frame, CancellationToken ct)
    {
        if (_memoryProvider is null || _options.MemorySchema is null)
            throw new InvalidOperationException("Memory provider and schema are required to serve QueryFrame.");

        var result = await _memoryProvider.QueryAsync(
            frame,
            _options.MemorySchema,
            _options.MemoryOptions ?? throw new InvalidOperationException("Memory options are required to serve QueryFrame."),
            ct).ConfigureAwait(false);

        return new CapsFrame
        {
            AnchorRef = _options.MemoryAnchorRef ?? _options.MemorySchema.TableName,
            Count = (uint)result.Rows.Count,
            Data = result.Rows.Select(ToJsonElement).ToList(),
            NextCursor = result.NextCursor,
            TokenEst = EstimateTokens(result.Rows),
            TokenizerUsed = "native-estimate",
            RequestId = frame.RequestId,
        };
    }

    private async Task<CapsFrame> DispatchActionAsync(
        ActionFrame frame,
        ulong wireInputBytes,
        CancellationToken ct)
    {
        if (_actionProvider is null)
            throw new InvalidOperationException("Action provider is required to serve ActionFrame.");
        var actionOptions = _options.ActionOptions
            ?? throw new InvalidOperationException("Action options are required to serve ActionFrame.");
        if (!actionOptions.Actions.TryGetValue(frame.ActionId, out var spec))
            throw new InvalidOperationException($"Unknown action_id '{frame.ActionId}'.");

        var timeout = frame.TimeoutMs == 0
            ? spec.TimeoutMsDefault ?? actionOptions.DefaultTimeoutMs
            : Math.Min(frame.TimeoutMs, Math.Min(spec.TimeoutMsMax ?? actionOptions.MaxTimeoutMs,
                actionOptions.MaxTimeoutMs));

        var context = new ActionContext
        {
            AgentNid = null,
            RequestId = frame.RequestId,
            Spec = spec,
            TimeoutMs = timeout,
            Priority = string.IsNullOrWhiteSpace(frame.Priority) ? "normal" : frame.Priority!,
            WireInputBytes = wireInputBytes,
        };
        await _actionProvider.AuthorizeAsync(frame, context, ct).ConfigureAwait(false);
        var result = await _actionProvider.ExecuteAsync(frame, context, ct).ConfigureAwait(false);

        return new CapsFrame
        {
            AnchorRef = result.AnchorRef ?? spec.ResultAnchor ?? string.Empty,
            Count = result.Result.HasValue ? 1u : 0u,
            Data = result.Result.HasValue ? [result.Result.Value.Clone()] : [],
            TokenEst = result.TokenEst,
            TokenizerUsed = "native-estimate",
            RequestId = frame.RequestId,
        };
    }

    private async Task<(IFrame Frame, ulong PayloadLength)> ReadFrameAsync(
        Stream stream,
        NcpEncodingPolicy encodingPolicy,
        CancellationToken ct)
    {
        var (header, rawHeader) = await ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false);
        encodingPolicy.EnsureAllows(header);
        var payload = new byte[header.PayloadLength];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        var wire = new byte[rawHeader.Length + payload.Length];
        rawHeader.CopyTo(wire, 0);
        payload.CopyTo(wire, rawHeader.Length);
        return (_codec.Decode(wire), header.PayloadLength);
    }

    private async Task WriteFrameAsync(Stream stream, IFrame frame, EncodingTier tier, CancellationToken ct)
    {
        var wire = _codec.Encode(frame, tier);
        await stream.WriteAsync(wire, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static ErrorFrame ToErrorFrame(NpsException ex) => new()
    {
        Status = ex.NpsStatusCode ?? NpsStatusCodes.ClientBadFrame,
        Error = ex.ProtocolErrorCode ?? "NWP-NATIVE-DISPATCH-FAILED",
        Message = ex.Message,
    };

    private static ErrorFrame ToDecodeErrorFrame(NpsException ex, string defaultError) => new()
    {
        Status = ex.NpsStatusCode ?? NpsStatusCodes.ClientBadFrame,
        Error = ex.ProtocolErrorCode ?? defaultError,
        Message = "Malformed NPS frame.",
    };

    private static JsonElement ToJsonElement(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static uint EstimateTokens(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(rows).Length;
        return (uint)Math.Max(1, jsonBytes / 4);
    }

    private static async Task<(FrameHeader header, byte[] raw)> ReadFrameHeaderAsync(Stream stream, CancellationToken ct)
    {
        var peek = new byte[2];
        await stream.ReadExactlyAsync(peek, ct).ConfigureAwait(false);
        var ext = ((FrameFlags)peek[1] & FrameFlags.Ext) != 0;
        var remaining = ext ? FrameHeader.ExtendedSize - 2 : FrameHeader.DefaultSize - 2;
        var rest = new byte[remaining];
        await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
        var raw = new byte[peek.Length + rest.Length];
        peek.CopyTo(raw, 0);
        rest.CopyTo(raw, peek.Length);
        return (FrameHeader.Parse(raw), raw);
    }
}

public sealed class NwpNativeNodeOptions
{
    public MemoryNodeSchema? MemorySchema { get; init; }
    public string? MemoryAnchorRef { get; init; }
    public MemoryNodeOptions? MemoryOptions { get; init; }
    public ActionNodeOptions? ActionOptions { get; init; }
}
