// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Exceptions;

namespace NPS.Core.Ncp;

internal static class SchemaBatchStreamTransport
{
    internal static async ValueTask CloseAfterFailureAsync(NcpSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (IOException) { } // Preserve the primary operation fault during cleanup.
        catch (ObjectDisposedException) { }
    }

    internal static SchemaBatchLimits BindLimits(NcpSession session, SchemaBatchLimits? configured)
    {
        var limits = configured ?? new();
        limits.Validate();
        if (!session.SelectedFeatures.Contains(new Alpha20.FeatureSelection(SchemaBatchReceiver.FeatureId, 1)))
            throw new NpsException("Schema-batch revision is not selected.", NpsStatusCodes.ClientConflict, NcpErrorCodes.FeatureRequiredUnsupported);
        return limits with { MaxPayloadBytes = Math.Min(limits.MaxPayloadBytes, session.ServerCaps.MaxFramePayload ?? Frames.FrameHeader.DefaultMaxPayload) };
    }

    internal static TimeSpan WaitBudget(TimeSpan? configured)
    {
        var budget = configured ?? TimeSpan.FromSeconds(30);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(configured));
        return budget;
    }

    internal static async Task<byte[]> ReadAsync(NcpSession session, uint maxPayload, TimeSpan wait, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(wait);
        try
        {
            var (header, raw) = await NcpNativeClient.ReadFrameHeaderAsync(session.GetStream(), bounded.Token).ConfigureAwait(false);
            session.EncodingPolicy.EnsureAllows(header);
            if (header.PayloadLength > maxPayload)
                throw new NpsException("Schema-batch frame exceeds bound.", NpsStatusCodes.LimitPayload, NcpErrorCodes.FramePayloadTooLarge);
            var wire = new byte[raw.Length + header.PayloadLength];
            raw.CopyTo(wire, 0);
            await session.GetStream().ReadExactlyAsync(wire.AsMemory(raw.Length), bounded.Token).ConfigureAwait(false);
            return wire;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Timed out awaiting a bounded schema-batch frame."); }
    }
}
