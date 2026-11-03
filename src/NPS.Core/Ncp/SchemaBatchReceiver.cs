// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>Immutable published state. Epoch/version are absent for unversioned unary Caps.</summary>
public sealed record SchemaBatchSnapshot(string AnchorRef, string? EpochId, ulong? Version,
    IReadOnlyList<JsonElement> Rows, long RetainedBytes);

/// <summary>Local lower limits for the experimental SB-07 reference ceilings.</summary>
public sealed record SchemaBatchLimits
{
    public uint MaxPayloadBytes { get; init; } = 65536;
    public int MaxRows { get; init; } = 100000;
    public long MaxRetainedBytes { get; init; } = 33554432;
    public int MaxRowsPerChunk { get; init; } = 512;
    public uint InitialCredit { get; init; } = 8;

    internal void Validate()
    {
        if (MaxPayloadBytes is 0 or > 65536 || MaxRows is < 1 or > 100000
            || MaxRetainedBytes is < 2 or > 33554432 || MaxRowsPerChunk is < 1 or > 512 || InitialCredit > 8)
            throw new ArgumentOutOfRangeException(nameof(SchemaBatchLimits));
    }
}

/// <summary>
/// Bounded, serialized encoded-frame receiver for candidate nps.schema_batches=1.
/// The host supplies the actual admitted selection, producer/source authorization,
/// transport policy and lifetime. This helper performs no negotiation/authentication,
/// offers no feature and is not thread safe. Any wire fault/cancellation is terminal;
/// recovery requires a new authorized receiver and complete baseline.
/// </summary>
public sealed class SchemaBatchReceiver : IDisposable
{
    public const string FeatureId = "nps.schema_batches";
    private readonly SchemaBatchLimits _limits;
    private readonly TimeProvider _time;
    private AnchorFrame? _anchor;
    private DateTimeOffset _expiresAt;
    private long _anchorTimestamp;
    private string? _epoch;
    private uint _nextChunk;
    private uint _credit;
    private List<JsonElement> _staged = [];
    private List<int> _rowBytes = [];
    private long _retainedBytes = 2;
    private SchemaBatchSnapshot? _snapshot;
    private bool _terminal;
    internal NcpEncodingPolicy? SessionEncodingPolicy { get; init; }

    /// <summary>
    /// Constructs a local receiver only for an exact, host-verified admitted revision.
    /// Passing selection objects is not proof of authority; hosts must validate them
    /// on the real connection and cannot expose this constructor as caller admission.
    /// </summary>
    public SchemaBatchReceiver(IReadOnlyList<FeatureSelection> admittedFeatures,
        SchemaBatchLimits? limits = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(admittedFeatures);
        if (admittedFeatures.Count > 32 || admittedFeatures.Count(f => f.Id == FeatureId) != 1
            || !admittedFeatures.Contains(new FeatureSelection(FeatureId, 1)))
            throw Fault(NpsStatusCodes.ClientConflict, NcpErrorCodes.FeatureRequiredUnsupported);
        _limits = limits ?? new();
        _limits.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _credit = _limits.InitialCredit;
    }

    /// <summary>Only complete immutable state is visible. Expired anchors invalidate the receiver.</summary>
    public SchemaBatchSnapshot? Snapshot
    {
        get
        {
            CheckActive();
            try { CheckAnchorTime(); return _snapshot; }
            catch (NpsException) { Terminate(); throw; }
        }
    }

    /// <summary>True after a fault, explicit cancellation or disposal; no retained state remains.</summary>
    public bool IsTerminal => _terminal;
    internal uint RemainingCredit => _credit;

    /// <summary>
    /// Accepts exactly one complete bounded encoded frame. No partial state is published.
    /// The host must also bound transport allocation and enforce its negotiated tier.
    /// </summary>
    public void Receive(ReadOnlySpan<byte> wire, CancellationToken cancellationToken = default)
    {
        CheckActive();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = NpsFrameCodec.PeekHeader(wire);
            SessionEncodingPolicy?.EnsureAllows(header);
            CheckAnchorTime();
            var decoded = SchemaBatchFrameCodec.Decode(wire, _limits.MaxPayloadBytes);
            switch (decoded)
            {
                case AnchorFrame anchor: SetAnchor(anchor); break;
                case StreamFrame stream: ReceiveChunk(stream, cancellationToken); break;
                case DiffFrame diff: ReceiveDiff(diff, cancellationToken); break;
                case CapsFrame caps: ReceiveCaps(caps, cancellationToken); break;
                default: throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameUnknownType);
            }
        }
        catch (OperationCanceledException) { Terminate(); throw; }
        catch (NpsException ex)
        {
            Terminate();
            if (ex.NpsStatusCode is not null && ex.ProtocolErrorCode is not null) throw;
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException
            or ArgumentException or OverflowException)
        {
            Terminate();
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
        }
    }

    /// <summary>
    /// Consumer grants bounded absolute credit after processing. Send the returned
    /// reverse control frame on the actual binding; peer data cannot call this method.
    /// </summary>
    public StreamFrame GrantCredit(uint credit)
    {
        CheckActive();
        if (credit > 8) throw new ArgumentOutOfRangeException(nameof(credit));
        if (_epoch is null || _snapshot is not null) throw new InvalidOperationException("No receiving baseline.");
        try { CheckAnchorTime(); }
        catch (NpsException) { Terminate(); throw; }
        _credit = credit;
        return new StreamFrame { StreamId = _epoch, Seq = _nextChunk, IsLast = false, Data = [], WindowSize = credit };
    }

    /// <summary>Clears owned state. Already delivered immutable snapshots cannot be retracted.</summary>
    public void Dispose() => Terminate();

    private void SetAnchor(AnchorFrame anchor)
    {
        if (_anchor is not null || _epoch is not null || _snapshot is not null)
            throw Fault(NpsStatusCodes.ClientConflict, NcpErrorCodes.AnchorStale);
        if (!string.Equals(anchor.AnchorId, SchemaBatchValues.CheckSchema(anchor.Schema, anchor.Ttl), StringComparison.Ordinal))
            throw Fault(NpsStatusCodes.ClientConflict, NcpErrorCodes.AnchorIdMismatch);
        // Decode owns this frame/schema; no caller-owned mutable schema collection is retained.
        _anchor = anchor;
        _expiresAt = _time.GetUtcNow().AddSeconds(anchor.Ttl);
        _anchorTimestamp = _time.GetTimestamp();
    }

    private void ReceiveChunk(StreamFrame frame, CancellationToken ct)
    {
        if (_snapshot is not null || frame.ErrorCode is not null || frame.Seq != _nextChunk
            || frame.Seq == uint.MaxValue && !frame.IsLast)
            throw Fault(NpsStatusCodes.StreamSeqGap, NcpErrorCodes.StreamSeqGap);
        if (_epoch is null)
        {
            if (!Guid.TryParseExact(frame.StreamId, "D", out var id) || id.Version != 4
                || frame.AnchorRef is null)
                throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
            _epoch = frame.StreamId;
            if (frame.WindowSize is { } first) _credit = Math.Min(_credit, first);
        }
        else if (!string.Equals(_epoch, frame.StreamId, StringComparison.Ordinal))
            throw Fault(NpsStatusCodes.StreamNotFound, NcpErrorCodes.StreamNotFound);
        else if (frame.WindowSize is not null)
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
        CheckSchema(frame.AnchorRef, allowOmitted: _nextChunk != 0);
        if (_credit == 0) throw Fault(NpsStatusCodes.StreamLimit, NcpErrorCodes.StreamWindowOverflow);
        AddRows(frame.Data, ct);
        _credit--;
        if (frame.IsLast)
        {
            ct.ThrowIfCancellationRequested();
            _snapshot = Publish(_epoch, 0, _staged, _retainedBytes);
            _staged = [];
        }
        else _nextChunk++;
    }

    private void ReceiveCaps(CapsFrame frame, CancellationToken ct)
    {
        if (_epoch is not null || _snapshot is not null || frame.NextCursor is not null)
            throw Fault(NpsStatusCodes.StreamSeqGap, NcpErrorCodes.StreamSeqGap);
        if (frame.InlineAnchor is not null) SetAnchor(frame.InlineAnchor);
        CheckSchema(frame.AnchorRef);
        if (frame.Data is null || frame.Count != frame.Data.Count)
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
        AddRows(frame.Data, ct);
        ct.ThrowIfCancellationRequested();
        _snapshot = Publish(null, null, _staged, _retainedBytes);
        _staged = [];
    }

    private void AddRows(IReadOnlyList<JsonElement> rows, CancellationToken ct)
    {
        if (rows is null || rows.Count > _limits.MaxRowsPerChunk || rows.Count > _limits.MaxRows - _staged.Count)
            throw Fault(NpsStatusCodes.StreamLimit, NcpErrorCodes.StreamLimitExceeded);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            SchemaBatchValues.CheckRow(_anchor!.Schema, row);
            var compact = JsonSerializer.SerializeToUtf8Bytes(row);
            _retainedBytes += compact.Length + (_staged.Count == 0 ? 0 : 1);
            if (_retainedBytes > _limits.MaxRetainedBytes)
                throw Fault(NpsStatusCodes.StreamLimit, NcpErrorCodes.StreamLimitExceeded);
            // Retain the counted representation, not raw peer whitespace carried by
            // JsonElement.Clone(). JsonElement.Parse owns a compact row independently.
            _staged.Add(JsonElement.Parse(compact));
            _rowBytes.Add(compact.Length);
        }
    }

    private void ReceiveDiff(DiffFrame frame, CancellationToken ct)
    {
        if (_snapshot?.EpochId is null || !string.Equals(frame.EntityId, _epoch, StringComparison.Ordinal))
            throw Fault(NpsStatusCodes.StreamNotFound, NcpErrorCodes.StreamNotFound);
        CheckSchema(frame.AnchorRef);
        if (frame.BaseSeq != _snapshot.Version || frame.BaseSeq == ulong.MaxValue)
            throw Fault(NpsStatusCodes.StreamSeqGap, NcpErrorCodes.StreamSeqGap);
        if (frame.PatchFormat != NcpPatchFormat.JsonPatch || frame.BinaryBitset is not null)
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.DiffFormatUnsupported);
        if (frame.Patch is not { Count: >= 1 and <= 256 })
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
        var changedRows = new Dictionary<int, JsonElement[]>();
        var seen = new HashSet<(int Row, int Field)>();
        foreach (var patch in frame.Patch)
        {
            ct.ThrowIfCancellationRequested();
            if (patch is null || patch.Op != "replace" || patch.From is not null || patch.Value is null || patch.Path is null)
                throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
            var path = patch.Path.Split('/');
            if (path.Length != 3 || path[0].Length != 0 || !SchemaBatchValues.Index(path[1], out var row)
                || !SchemaBatchValues.Index(path[2], out var field) || row >= _snapshot.Rows.Count
                || field >= _anchor!.Schema.Fields.Count || !seen.Add((row, field)))
                throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
            SchemaBatchValues.CheckValue(patch.Value.Value, _anchor.Schema.Fields[field]);
            if (!changedRows.TryGetValue(row, out var cells))
                changedRows[row] = cells = _snapshot.Rows[row].EnumerateArray().ToArray();
            cells[field] = patch.Value.Value;
        }
        var rows = _snapshot.Rows.ToArray();
        var sizes = _rowBytes.ToArray();
        var retained = _retainedBytes;
        foreach (var (row, cells) in changedRows)
        {
            ct.ThrowIfCancellationRequested();
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var cell in cells) cell.WriteTo(writer);
                writer.WriteEndArray();
            }
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            retained += buffer.WrittenCount - sizes[row];
            sizes[row] = buffer.WrittenCount;
            rows[row] = document.RootElement.Clone();
        }
        if (retained > _limits.MaxRetainedBytes)
            throw Fault(NpsStatusCodes.StreamLimit, NcpErrorCodes.StreamLimitExceeded);
        ct.ThrowIfCancellationRequested();
        _snapshot = Publish(_epoch, frame.BaseSeq + 1, rows, retained);
        _rowBytes = sizes.ToList();
        _retainedBytes = retained;
    }

    private SchemaBatchSnapshot Publish(string? epoch, ulong? version,
        IEnumerable<JsonElement> rows, long retainedBytes) =>
        new(_anchor!.AnchorId, epoch, version, Array.AsReadOnly(rows.ToArray()), retainedBytes);

    private void CheckSchema(string? anchorRef, bool allowOmitted = false)
    {
        if (_anchor is null) throw Fault(NpsStatusCodes.ClientNotFound, NcpErrorCodes.AnchorNotFound);
        if (anchorRef is null && !allowOmitted)
            throw Fault(NpsStatusCodes.ClientBadFrame, NcpErrorCodes.AnchorSchemaInvalid);
        if (anchorRef is not null && !string.Equals(anchorRef, _anchor.AnchorId, StringComparison.Ordinal))
            throw Fault(NpsStatusCodes.ClientConflict, NcpErrorCodes.AnchorStale);
    }

    private void CheckAnchorTime()
    {
        if (_anchor is not null && (_time.GetUtcNow() >= _expiresAt
            || _time.GetElapsedTime(_anchorTimestamp) >= TimeSpan.FromSeconds(_anchor.Ttl)))
            throw Fault(NpsStatusCodes.ClientConflict, NcpErrorCodes.AnchorStale);
    }

    private void CheckActive()
    {
        if (_terminal) throw new InvalidOperationException("Receiver terminated; authorize a complete new baseline.");
    }

    private void Terminate()
    {
        _terminal = true;
        _anchor = null;
        _snapshot = null;
        _epoch = null;
        _staged = [];
        _rowBytes = [];
        _retainedBytes = 0;
        _credit = 0;
    }

    private static NpsException Fault(string status, string code) => new("Schema batch rejected.", status, code);
}
