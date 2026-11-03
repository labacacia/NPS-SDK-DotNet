// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.Core.Exceptions;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

// Actual receiver consumes shared fixed bytes. Selection/time/lower limits are
// trusted fixture setup, never remote identity/admission. Real mTLS is separate.
public sealed class SchemaBatchPortableVectorTests
{
    public static IEnumerable<object[]> Cases()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "spec/conformance/alpha20/schema_batch_vectors.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var cases = document.RootElement.GetProperty("cases");
            Assert.Equal(106, cases.GetArrayLength());
            foreach (var item in cases.EnumerateArray()) yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
            yield break;
        }
        throw new FileNotFoundException("Shared fixed schema-batch frames are required.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedFixedFramesExecuteAtTheReceiverBoundary(string id, string rawCase)
    {
        Assert.StartsWith("SB-F", id);
        using var document = JsonDocument.Parse(rawCase);
        var scenario = document.RootElement;
        var fixture = scenario.GetProperty("trusted_fixture");
        var expected = scenario.GetProperty("expected");
        var clock = new Clock();
        SchemaBatchReceiver? receiver = null;
        string? outcome = null;
        bool terminal;
        try
        {
            receiver = Create(fixture, clock);
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                switch (step.GetProperty("operation").GetString())
                {
                    case "receive": receiver.Receive(Convert.FromHexString(step.GetProperty("wire_hex").GetString()!)); break;
                    case "cancel_receive":
                        using (var canceled = new CancellationTokenSource())
                        {
                            canceled.Cancel();
                            receiver.Receive(Convert.FromHexString(step.GetProperty("wire_hex").GetString()!), canceled.Token);
                        }
                        break;
                    case "advance":
                        clock.Elapsed += TimeSpan.FromSeconds(step.GetProperty("elapsed_seconds").GetDouble());
                        clock.Wall += TimeSpan.FromSeconds(step.GetProperty("wall_seconds").GetDouble());
                        break;
                    case "credit":
                        var ack = receiver.GrantCredit(step.GetProperty("value").GetUInt32());
                        Assert.Empty(ack.Data);
                        Assert.False(ack.IsLast);
                        break;
                    case "inspect": _ = receiver.Snapshot; break;
                    case "reset":
                        receiver.Dispose();
                        receiver = Create(fixture, clock);
                        break;
                    default: Assert.Fail("Unknown shared fixture operation."); break;
                }
            }
            var snapshot = receiver.Snapshot;
            if (snapshot is null) outcome = "STAGED";
            else
            {
                outcome = snapshot.EpochId is null ? "UNVERSIONED" : "PUBLISHED";
                Assert.True(JsonElement.DeepEquals(expected.GetProperty("rows"), JsonSerializer.SerializeToElement(snapshot.Rows)), id);
                if (expected.GetProperty("version").ValueKind == JsonValueKind.Null) Assert.Null(snapshot.Version);
                else Assert.Equal(expected.GetProperty("version").GetUInt64(), snapshot.Version);
            }
        }
        catch (NpsException error) { outcome = error.ProtocolErrorCode; }
        catch (OperationCanceledException) { outcome = "CANCELED"; }
        finally
        {
            // Capture the pre-disposal terminal result; disposal itself must not manufacture a pass.
            terminal = receiver?.IsTerminal ?? true;
            receiver?.Dispose();
        }
        Assert.Equal(expected.GetProperty("outcome").GetString(), outcome);
        Assert.Equal(expected.GetProperty("terminal").GetBoolean(), terminal);
    }

    private static SchemaBatchReceiver Create(JsonElement fixture, Clock clock)
    {
        var revision = Number(fixture, "selected_revision", 1);
        return new(revision == 0 ? [] : [new FeatureSelection(SchemaBatchReceiver.FeatureId, (ushort)revision)], new()
        {
            MaxPayloadBytes = (uint)Number(fixture, "max_payload_bytes", 65536),
            MaxRows = Number(fixture, "max_rows", 100000),
            MaxRowsPerChunk = Number(fixture, "max_rows_per_chunk", 512),
            MaxRetainedBytes = Number(fixture, "max_retained_bytes", 33554432),
            InitialCredit = (uint)Number(fixture, "initial_credit", 8),
        }, clock);
    }
    private static int Number(JsonElement value, string field, int fallback) => value.TryGetProperty(field, out var number) ? number.GetInt32() : fallback;

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan Wall { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Wall;
    }
}
