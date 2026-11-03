// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Codecs;
using NPS.Core.Frames;

namespace NPS.Core.Ncp;

/// <summary>
/// Dedicated-session reader returning only complete authorized baseline/update state.
/// Host callbacks bind actual peer/source policy. No feature advertisement, automatic
/// retry or reconnect inheritance; failures close the dedicated session and receiver.
/// </summary>
public sealed class SchemaBatchStreamReader : IDisposable
{
    private readonly NcpSession _session;
    private readonly SchemaBatchReceiver _receiver;
    private readonly Func<CancellationToken, ValueTask> _authorize;
    private readonly SchemaBatchLimits _limits;
    private readonly TimeSpan _readWait;
    private bool _complete, _terminal;
    private int _busy;

    /// <summary>Constructs one current-policy reader with a positive configured credit target.</summary>
    public SchemaBatchStreamReader(NcpSession session, Func<CancellationToken, ValueTask> authorizeDelivery,
        SchemaBatchLimits? limits = null, TimeSpan? readWait = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(authorizeDelivery);
        _session = session; _authorize = authorizeDelivery;
        _limits = SchemaBatchStreamTransport.BindLimits(session, limits);
        if (_limits.InitialCredit == 0) throw new ArgumentOutOfRangeException(nameof(limits));
        _readWait = SchemaBatchStreamTransport.WaitBudget(readWait);
        _receiver = session.CreateSchemaBatchReceiver(_limits, timeProvider);
    }

    /// <summary>Reads a complete baseline first, then one exact-bound update per call.</summary>
    public async Task<SchemaBatchSnapshot> ReadNextAsync(CancellationToken ct = default)
    {
        if (_terminal) throw new InvalidOperationException("Schema-batch reader terminated.");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Serialize schema-batch reads.");
        try
        {
            var prior = _complete ? _receiver.Snapshot : null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await _authorize(ct).ConfigureAwait(false);
                var wire = await SchemaBatchStreamTransport.ReadAsync(_session, _limits.MaxPayloadBytes, _readWait, ct).ConfigureAwait(false);
                await _authorize(ct).ConfigureAwait(false);
                _receiver.Receive(wire, ct);
                var snapshot = _receiver.Snapshot;
                if (snapshot is not null)
                {
                    if (prior is not null && snapshot.Version != prior.Version + 1)
                        throw new Exceptions.NpsException("Invalid schema-batch update.", NpsStatusCodes.StreamSeqGap, NcpErrorCodes.StreamSeqGap);
                    _complete = true;
                    return snapshot;
                }
                if (NpsFrameCodec.PeekHeader(wire).FrameType == FrameType.Stream && _receiver.RemainingCredit == 0)
                {
                    await _authorize(ct).ConfigureAwait(false);
                    var credit = _session.EncodeSchemaBatchFrame(_receiver.GrantCredit(_limits.InitialCredit));
                    await _session.GetStream().WriteAsync(credit, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            Dispose();
            await SchemaBatchStreamTransport.CloseAfterFailureAsync(_session).ConfigureAwait(false);
            throw;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>Clears receiver data; the caller owns normal dedicated-session disposal.</summary>
    public void Dispose()
    {
        _terminal = true;
        _receiver.Dispose();
    }
}
