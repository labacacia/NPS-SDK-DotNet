// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text.Json;
using MessagePack;
using NPS.Core.Exceptions;
using NPS.Core.Frames;

namespace NPS.Core.Ncp;

// Validate before typed decode can normalize a float 42.0 to integer 42 or lose
// duplicate members. All reference numeric fields/cells are integer-only.
internal static class SchemaBatchWireGuard
{
    internal static void Validate(ReadOnlySpan<byte> payload, EncodingTier tier, FrameType frameType, bool strictValues = true)
    {
        if (tier == EncodingTier.Json)
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 32 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.OrdinalIgnoreCase));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                    throw Invalid();
                else if (strictValues && reader.TokenType == JsonTokenType.Number && reader.ValueSpan.ContainsAny((byte)'.', (byte)'e', (byte)'E'))
                    throw Invalid();
            }
            return;
        }
        if (tier != EncodingTier.MsgPack) throw Invalid();
        try
        {
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(payload.ToArray()));
            ReadValue(ref reader, 0, frameType, strictValues, featureSelection: false);
            if (!reader.End) throw Invalid();
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException)
        { throw Invalid(); }
    }

    private static void ReadValue(ref MessagePackReader reader, int depth, FrameType frameType,
        bool strictValues, bool featureSelection)
    {
        if (depth > 32) throw Invalid();
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Map:
                var count = reader.ReadMapHeader();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    if (reader.NextMessagePackType != MessagePackType.String) throw Invalid();
                    var name = reader.ReadString()!;
                    if (!names.Add(name)) throw Invalid();
                    ReadValue(ref reader, depth + 1, frameType, strictValues,
                        featureSelection || name is "SelectedFeatures" or "selected_features");
                }
                break;
            case MessagePackType.Array:
                var length = reader.ReadArrayHeader();
                for (var i = 0; i < length; i++) ReadValue(ref reader, depth + 1, frameType, strictValues, featureSelection);
                break;
            case MessagePackType.Float:
            case MessagePackType.Extension:
                if (strictValues || featureSelection) throw Invalid();
                reader.Skip();
                break;
            case MessagePackType.Binary:
                if (strictValues || featureSelection) throw frameType == FrameType.Diff
                    ? new NpsException("Unsupported schema batch diff.", NpsStatusCodes.ClientBadFrame, NcpErrorCodes.DiffFormatUnsupported)
                    : Invalid();
                reader.Skip();
                break;
            default:
                reader.Skip();
                break;
        }
    }

    private static NpsException Invalid() =>
        new("Schema batch wire rejected.", NpsStatusCodes.ClientBadFrame, NcpErrorCodes.FrameFlagsInvalid);
}
