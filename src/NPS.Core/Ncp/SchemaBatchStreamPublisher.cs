// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>Host-owned immutable capture; the host keeps rows alive until publication ends.</summary>
public sealed record SchemaBatchSourceSnapshot(FrameSchema Schema, IReadOnlyList<JsonElement> Rows);

/// <summary>Local write counters only; never receiver-commit, delivery or authority evidence.</summary>
public sealed record SchemaBatchSendReceipt(string EpochId, string AnchorRef, int RowCount,
    int FramesWritten, long BaselineTupleBytes, ulong Version);

/// <summary>Bounded local progress without rows, field names, credentials or arbitrary peer text.</summary>
public sealed record SchemaBatchSendProgress(string Stage, int FramesWritten, int RowsWritten);

/// <summary>
/// Dedicated-session reference publisher with current host source authorization and
/// bounded reverse credit. No registration, background queue, automatic retry or remote
/// commit acknowledgment. Caller owns immutable source capture and cancellation on revocation.
/// </summary>
public sealed class SchemaBatchStreamPublisher
{
    private readonly NcpSession _session;
    private readonly Func<CancellationToken, ValueTask> _authorize;
    private readonly SchemaBatchLimits _limits;
    private readonly TimeSpan _creditWait;
    private readonly TimeProvider _time;
    private readonly Action<SchemaBatchSendProgress>? _progress;
    private FrameSchema? _schema;
    private string? _epoch, _anchor;
    private uint _nextSequence;
    private ulong _version;
    private int _framesWritten, _rowsWritten, _rowCount, _busy;
    private long _tupleBytes = 2, _schemaTimestamp;
    private DateTimeOffset _schemaExpiresAt;
    private uint _schemaTtl;
    private bool _started, _complete, _terminal;

    /// <summary>
    /// Requires an actual selected session and a trusted host callback bound to its
    /// authenticated peer/source. Progress callbacks are local and may fail closed.
    /// </summary>
    public SchemaBatchStreamPublisher(NcpSession session, Func<CancellationToken, ValueTask> authorizeSource,
        SchemaBatchLimits? limits = null, TimeSpan? creditWait = null, TimeProvider? timeProvider = null,
        Action<SchemaBatchSendProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(authorizeSource);
        _session = session; _authorize = authorizeSource;
        _limits = SchemaBatchStreamTransport.BindLimits(session, limits);
        _creditWait = SchemaBatchStreamTransport.WaitBudget(creditWait);
        _time = timeProvider ?? TimeProvider.System;
        _progress = progress;
    }

    /// <summary>
    /// Authorizes before capture, then publishes a fresh complete baseline. Only one
    /// baseline is allowed; faults close the dedicated session. Receipt is local writes only.
    /// </summary>
    public async Task<SchemaBatchSendReceipt> PublishAsync(
        Func<CancellationToken, ValueTask<SchemaBatchSourceSnapshot>> capture,
        uint schemaTtlSeconds = 300, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Enter();
        try
        {
            if (_started) throw new InvalidOperationException("A fresh authorized publisher is required.");
            _started = true;
            ct.ThrowIfCancellationRequested();
            await _authorize(ct).ConfigureAwait(false);
            var source = await capture(ct).ConfigureAwait(false);
            if (source?.Rows is null || source.Schema?.Fields is null) throw Invalid();
            if (source.Rows.Count > _limits.MaxRows) throw Limit();
            _schema = new() { Fields = source.Schema.Fields.ToArray() };
            _anchor = SchemaBatchValues.CheckSchema(_schema, schemaTtlSeconds);
            _epoch = Guid.NewGuid().ToString();
            _rowCount = source.Rows.Count;
            _schemaTtl = schemaTtlSeconds;
            _schemaTimestamp = _time.GetTimestamp();
            _schemaExpiresAt = _time.GetUtcNow().AddSeconds(schemaTtlSeconds);
            await WriteAsync(new AnchorFrame { AnchorId = _anchor, Schema = _schema, Ttl = schemaTtlSeconds }, "wrote_anchor", ct).ConfigureAwait(false);

            uint credit = 1; // Bootstrap safely before the receiver has granted a larger window.
            var offset = 0;
            do
            {
                if (credit == 0) credit = await ReadCreditAsync(ct).ConfigureAwait(false);
                var count = Math.Min(_limits.MaxRowsPerChunk, source.Rows.Count - offset);
                var rows = new JsonElement[count];
                for (var index = 0; index < count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = source.Rows[offset + index];
                    SchemaBatchValues.CheckRow(_schema, row);
                    _tupleBytes += JsonSerializer.SerializeToUtf8Bytes(row).Length + (offset + index == 0 ? 0 : 1);
                    if (_tupleBytes > _limits.MaxRetainedBytes) throw Limit();
                    rows[index] = row;
                }
                var last = offset + count == source.Rows.Count;
                var frame = new StreamFrame { StreamId = _epoch, Seq = _nextSequence, IsLast = last,
                    AnchorRef = _nextSequence == 0 ? _anchor : null, Data = rows,
                    WindowSize = _nextSequence == 0 ? 1U : null };
                await WriteAsync(frame, "wrote_chunk", ct).ConfigureAwait(false);
                offset += count;
                credit--;
                if (last) break;
                _nextSequence++;
            } while (true);
            _complete = true;
            return Receipt();
        }
        catch
        {
            _terminal = true;
            await SchemaBatchStreamTransport.CloseAfterFailureAsync(_session).ConfigureAwait(false);
            throw;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>
    /// Sends an exact-base typed replace update for the established epoch. The host
    /// supplies correct source changes; local write completion does not prove remote application.
    /// </summary>
    public async Task<SchemaBatchSendReceipt> SendDiffAsync(ulong expectedBaseVersion,
        IReadOnlyList<JsonPatchOperation> patches, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patches);
        Enter();
        try
        {
            if (!_complete) throw new InvalidOperationException("A complete baseline is required.");
            if (_version != expectedBaseVersion || _version == ulong.MaxValue)
                throw new NpsException("Invalid schema-batch base.", NpsStatusCodes.StreamSeqGap, NcpErrorCodes.StreamSeqGap);
            if (patches.Count is < 1 or > 256) throw Invalid();
            var seen = new HashSet<(int, int)>();
            foreach (var patch in patches)
            {
                if (patch is null || patch.Op != "replace" || patch.From is not null || patch.Value is null || patch.Path is null) throw Invalid();
                var path = patch.Path.Split('/');
                if (path.Length != 3 || path[0].Length != 0 || !SchemaBatchValues.Index(path[1], out var row)
                    || !SchemaBatchValues.Index(path[2], out var field) || row >= _rowCount
                    || field >= _schema!.Fields.Count || !seen.Add((row, field))) throw Invalid();
                SchemaBatchValues.CheckValue(patch.Value.Value, _schema.Fields[field]);
            }
            await WriteAsync(new DiffFrame { AnchorRef = _anchor!, EntityId = _epoch, BaseSeq = _version,
                Patch = patches.ToArray() }, "wrote_diff", ct).ConfigureAwait(false);
            _version++;
            return Receipt();
        }
        catch
        {
            _terminal = true;
            await SchemaBatchStreamTransport.CloseAfterFailureAsync(_session).ConfigureAwait(false);
            throw;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task WriteAsync(IFrame frame, string stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckSchemaTime();
        await _authorize(ct).ConfigureAwait(false);
        var wire = _session.EncodeSchemaBatchFrame(frame);
        if ((uint)Codecs.NpsFrameCodec.PeekHeader(wire).PayloadLength > _limits.MaxPayloadBytes)
            throw new NpsException("Schema-batch frame exceeds bound.", NpsStatusCodes.LimitPayload, NcpErrorCodes.FramePayloadTooLarge);
        await _authorize(ct).ConfigureAwait(false);
        CheckSchemaTime();
        await _session.GetStream().WriteAsync(wire, ct).ConfigureAwait(false);
        _framesWritten++;
        if (frame is StreamFrame chunk) _rowsWritten += chunk.Data.Count;
        _progress?.Invoke(new(stage, _framesWritten, _rowsWritten));
    }

    private async Task<uint> ReadCreditAsync(CancellationToken ct)
    {
        while (true)
        {
            CheckSchemaTime();
            await _authorize(ct).ConfigureAwait(false);
            _progress?.Invoke(new("awaiting_credit", _framesWritten, _rowsWritten));
            var wire = await SchemaBatchStreamTransport.ReadAsync(_session, _limits.MaxPayloadBytes, _creditWait, ct).ConfigureAwait(false);
            if (SchemaBatchFrameCodec.Decode(wire, _limits.MaxPayloadBytes) is not StreamFrame control
                || control.IsLast || control.Data is not { Count: 0 } || control.WindowSize is null
                || control.AnchorRef is not null || control.ErrorCode is not null) throw Invalid();
            if (control.StreamId != _epoch)
                throw new NpsException("Unknown schema-batch epoch.", NpsStatusCodes.StreamNotFound, NcpErrorCodes.StreamNotFound);
            if (control.Seq != _nextSequence)
                throw new NpsException("Stale schema-batch credit.", NpsStatusCodes.StreamSeqGap, NcpErrorCodes.StreamSeqGap);
            if (control.WindowSize > 8)
                throw new NpsException("Schema-batch credit exceeds bound.", NpsStatusCodes.StreamLimit, NcpErrorCodes.StreamWindowOverflow);
            if (control.WindowSize != 0) return control.WindowSize.Value;
        }
    }

    private void CheckSchemaTime()
    {
        if (_schema is not null && (_time.GetUtcNow() >= _schemaExpiresAt
            || _time.GetElapsedTime(_schemaTimestamp) >= TimeSpan.FromSeconds(_schemaTtl)))
            throw new NpsException("Schema-batch schema expired.", NpsStatusCodes.ClientConflict, NcpErrorCodes.AnchorStale);
    }

    private void Enter()
    {
        if (_terminal) throw new InvalidOperationException("Schema-batch publisher terminated.");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Serialize schema-batch publication.");
    }

    private SchemaBatchSendReceipt Receipt() => new(_epoch!, _anchor!, _rowCount, _framesWritten, _tupleBytes, _version);
    private static NpsException Invalid() => new("Invalid schema-batch publication.", NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
    private static NpsException Limit() => new("Schema-batch publication exceeds bound.", NpsStatusCodes.StreamLimit, NcpErrorCodes.StreamLimitExceeded);
}
