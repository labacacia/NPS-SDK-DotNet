// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using NPS.Core.Frames;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

public sealed partial class NcpSchemaBatchSessionTests
{
    private sealed class TransferSource
    {
        private readonly NamedBaselineRow[] _rows;
        public FrameSchema[] Schemas { get; }
        public object[] Bodies { get; }
        public string[] SchemaHashes { get; }
        public int Count => _rows.Length;

        public TransferSource(int count)
        {
            var fields = new SchemaField[] { new("id", "string"), new("period", "string"), new("reason", "string"),
                new("refunded", "bool"), new("amount_minor", "int32") };
            Schemas = [new() { Fields = fields }, new() { Fields = [.. fields, new("note", "string", Nullable: true)] }];
            SchemaHashes = Schemas.Select(SchemaBatchFrameCodec.ComputeAnchorId).ToArray();
            _rows = Enumerable.Range(0, count).Select(index =>
            {
                var period = index % 100 < 50 ? "baseline" : "current";
                var reason = new[] { "delivery", "quality", "duplicate", "other" }[index % 4];
                return new NamedBaselineRow($"order:{index:D6}", period, reason,
                    index % 100 < 10 || period == "current" && reason == "delivery" && index % 100 < 80,
                    1000 + index % 100 * 37);
            }).ToArray();
            var schemaBodies = Schemas.Select(schema => new NamedTransferSchema(schema.Fields
                .Select(field => new NamedTransferField(field.Name, field.Type, field.Nullable)).ToArray())).ToArray();
            var changed = _rows.Select(row => new NamedTransferRow(row.Id, row.Period, row.Reason, row.Refunded, row.AmountMinor)).ToArray();
            Bodies = [new NamedBaselineBody(schemaBodies[0], _rows), new NamedTransferBody(schemaBodies[1], changed)];
        }

        // Both arms share immutable named values, not two retained JSON DOMs or
        // preencoded responses. Native object-to-tuple projection remains timed.
        public SchemaBatchSourceSnapshot CaptureTuples(int variant)
        {
            var tuples = _rows.Select(row =>
            {
                object?[] values = variant == 0
                    ? [row.Id, row.Period, row.Reason, row.Refunded, row.AmountMinor]
                    : [row.Id, row.Period, row.Reason, row.Refunded, row.AmountMinor, null];
                return JsonSerializer.SerializeToElement(values);
            }).ToArray();
            return new(Schemas[variant], tuples);
        }

        public void Verify(TransferredSnapshot snapshot, int variant)
        {
            Assert.Equal(SchemaHashes[variant], snapshot.SchemaHash);
            Assert.Equal(Count, snapshot.NamedRows?.Count ?? snapshot.Rows.Count);
            long sum = 0;
            for (var index = 0; index < Count; index++)
            {
                var expected = _rows[index];
                if (snapshot.NamedRows is not null)
                {
                    var observed = snapshot.NamedRows[index];
                    Assert.Equal(expected.Id, observed.Id);
                    Assert.Equal(expected.Period, observed.Period);
                    Assert.Equal(expected.Reason, observed.Reason);
                    Assert.Equal(expected.Refunded, observed.Refunded);
                    Assert.Equal(expected.AmountMinor, observed.AmountMinor);
                    Assert.Null(observed.Note);
                    sum += observed.AmountMinor;
                }
                else
                {
                    Assert.True(snapshot.Positional);
                    var row = snapshot.Rows[index];
                    Assert.Equal(Schemas[variant].Fields.Count, row.GetArrayLength());
                    Assert.Equal(expected.Id, row[0].GetString());
                    Assert.Equal(expected.Period, row[1].GetString());
                    Assert.Equal(expected.Reason, row[2].GetString());
                    Assert.Equal(expected.Refunded, row[3].GetBoolean());
                    Assert.Equal(expected.AmountMinor, row[4].GetInt32());
                    if (variant == 1) Assert.Equal(JsonValueKind.Null, row[5].ValueKind);
                    sum += row[4].GetInt64();
                }
            }
            Assert.Equal(Enumerable.Range(0, Count).Sum(index => (long)(1000 + index % 100 * 37)), sum);
        }
    }

    private sealed record TransferredSnapshot(string SchemaHash, IReadOnlyList<JsonElement> Rows,
        bool Positional, string? Epoch, long? LogicalRetainedBytes, IReadOnlyList<NamedTransferRow>? NamedRows = null);

    // Ordinary object maps use the established serializers and named DTO decoders.
    // Separate row types keep the original schema at exactly five wire fields.
    [MessagePackObject]
    public sealed record NamedBaselineRow(
        [property: Key("id"), JsonPropertyName("id")] string Id,
        [property: Key("period"), JsonPropertyName("period")] string Period,
        [property: Key("reason"), JsonPropertyName("reason")] string Reason,
        [property: Key("refunded"), JsonPropertyName("refunded")] bool Refunded,
        [property: Key("amount_minor"), JsonPropertyName("amount_minor")] int AmountMinor);

    [MessagePackObject]
    public sealed record NamedTransferRow(
        [property: Key("id"), JsonPropertyName("id")] string Id,
        [property: Key("period"), JsonPropertyName("period")] string Period,
        [property: Key("reason"), JsonPropertyName("reason")] string Reason,
        [property: Key("refunded"), JsonPropertyName("refunded")] bool Refunded,
        [property: Key("amount_minor"), JsonPropertyName("amount_minor")] int AmountMinor,
        [property: Key("note"), JsonPropertyName("note")] string? Note = null);

    [MessagePackObject]
    public sealed record NamedTransferField(
        [property: Key("name"), JsonPropertyName("name")] string Name,
        [property: Key("type"), JsonPropertyName("type")] string Type,
        [property: Key("nullable"), JsonPropertyName("nullable")] bool Nullable);

    [MessagePackObject]
    public sealed record NamedTransferSchema(
        [property: Key("fields"), JsonPropertyName("fields")] NamedTransferField[] Fields);

    [MessagePackObject]
    public sealed record NamedBaselineBody(
        [property: Key("schema"), JsonPropertyName("schema")] NamedTransferSchema Schema,
        [property: Key("rows"), JsonPropertyName("rows")] NamedBaselineRow[] Rows);

    [MessagePackObject]
    public sealed record NamedTransferBody(
        [property: Key("schema"), JsonPropertyName("schema")] NamedTransferSchema Schema,
        [property: Key("rows"), JsonPropertyName("rows")] NamedTransferRow[] Rows);
}
