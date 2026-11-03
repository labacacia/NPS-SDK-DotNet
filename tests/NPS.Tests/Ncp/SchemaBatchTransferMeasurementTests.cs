// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace NPS.Tests.Ncp;

public sealed partial class NcpSchemaBatchSessionTests
{
    public static IEnumerable<object[]> TransferConditions()
    {
        var selected = Environment.GetEnvironmentVariable("NPS_NCP_TRANSFER_CASE");
        foreach (var scale in new[] { 100, 1000, 100000 })
        foreach (var arm in new[] { "http-json-identity", "http-json-gzip", "http-msgpack-identity", "http-msgpack-gzip", "ncp-json", "ncp-msgpack" })
        foreach (var warm in new[] { false, true })
        foreach (var churn in new[] { false, true })
        {
            var label = $"{scale}-{arm}-{(warm ? "warm" : "cold")}-{(churn ? "churn" : "stable")}";
            if (selected is null || selected == label) yield return [scale, arm, warm, churn, label];
        }
    }

    [Theory]
    [MemberData(nameof(TransferConditions))]
    public async Task ComparableTransferResourceEvidence(int scale, string arm, bool warm, bool churn, string label)
    {
        var output = Environment.GetEnvironmentVariable("NPS_NCP_TRANSFER_CAPTURE_DIR");
        var repeats = output is null ? 1 : 30;
        var warmups = output is null ? 0 : 3;
        using var assemblyFile = File.OpenRead(typeof(NcpSchemaBatchSessionTests).Assembly.Location);
        var assemblyHash = Convert.ToHexStringLower(SHA256.HashData(assemblyFile));
        var source = new TransferSource(scale);
        await using var exchange = new TransferExchange(source, arm);
        await exchange.StartAsync();
        var traffic = new TransferTraffic();
        using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var persistent = warm ? await exchange.OpenAsync(traffic, setupTimeout.Token) : null;
        if (persistent is not null) source.Verify(await persistent.FetchAsync(0, setupTimeout.Token), 0);
        var samples = new List<object>();
        string? observedHash = null, changedHash = null;
        for (var iteration = -warmups; iteration < repeats; iteration++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var bytesBefore = traffic.Total;
            var gzipBefore = exchange.GzipResponses;
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            TransferExchange.TransferFrontend? cold = null;
            TransferredSnapshot original, changed = null!;
            try
            {
                var frontend = persistent ?? (cold = await exchange.OpenAsync(traffic, timeout.Token));
                original = await frontend.FetchAsync(0, timeout.Token);
                if (churn) changed = await frontend.FetchAsync(1, timeout.Token);
                if (cold is not null) { await cold.DisposeAsync(); cold = null; }
            }
            finally { if (cold is not null) await cold.DisposeAsync(); }
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
            var wireBytes = traffic.Total - bytesBefore;
            var gzipResponses = exchange.GzipResponses - gzipBefore;
            Assert.True(milliseconds is >= 0 and < 120000);
            Assert.True(cpuMilliseconds >= 0 && allocatedBytes >= 0 && wireBytes > 0);
            Assert.Equal(arm.EndsWith("gzip", StringComparison.Ordinal) ? (churn ? 2 : 1) : 0, gzipResponses);
            source.Verify(original, 0); // Every value, after measurement, including retained old snapshot.
            if (churn)
            {
                source.Verify(changed, 1);
                Assert.NotEqual(original.SchemaHash, changed.SchemaHash);
                if (exchange.Native) Assert.NotEqual(original.Epoch, changed.Epoch);
            }
            observedHash ??= HashTransferredRows(original, source.Schemas[0].Fields.Select(field => field.Name).ToArray());
            if (churn) changedHash ??= HashTransferredRows(changed, source.Schemas[1].Fields.Select(field => field.Name).ToArray());
            if (iteration >= 0) samples.Add(new
            {
                iteration, wall_ms = milliseconds, cpu_ms = cpuMilliseconds, allocated_bytes = allocatedBytes,
                wire_bytes = wireBytes, gzip_responses = gzipResponses, rows = scale, verified_snapshots = churn ? 2 : 1,
                baseline_tuple_bytes = original.LogicalRetainedBytes, changed_tuple_bytes = churn ? changed.LogicalRetainedBytes : null,
            });
            if (output is not null && iteration >= 0)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, $"partial-{label}.json"), JsonSerializer.Serialize(new
                { case_id = label, completed_measured_iterations = samples.Count, samples }));
            }
            // The caller consumed/verified this iteration. Do not keep its large
            // snapshots in async state while the following request is decoding.
            original = null!;
            changed = null!;
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"transfer-{label}.json"), JsonSerializer.Serialize(new
            {
                case_id = label, scale, arm, warm, churn, repeats, warmups, source_seed = 20261102,
                test_assembly_sha256 = assemblyHash, source_storage = "shared-immutable-named-records-v1",
                execution_mode = Environment.GetEnvironmentVariable("NPS_NCP_TRANSFER_EXECUTION_MODE") ?? "test_platform",
                source_rows_sha256 = observedHash, changed_rows_sha256 = changedHash,
                schema_sha256 = source.SchemaHashes[0], changed_schema_sha256 = churn ? source.SchemaHashes[1] : null,
                processor_count = Environment.ProcessorCount, gc_heap_hard_limit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
                tcp_nodelay_requested = true,
                runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                build_configuration = typeof(NcpSchemaBatchSessionTests).Assembly.GetCustomAttributes(false)
                    .OfType<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration,
                traffic_boundary = "client TCP stream below TLS; both directions incl TLS/HTTP/native handshake/framing/credit; excludes TCP/IP",
                cpu_allocation_boundary = "co-host client/server workflow including client cold shutdown/counter overhead; not protocol attribution",
                samples,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string HashTransferredRows(TransferredSnapshot snapshot, string[] fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            if (snapshot.NamedRows is not null)
            {
                foreach (var row in snapshot.NamedRows)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(row.Id);
                    writer.WriteStringValue(row.Period);
                    writer.WriteStringValue(row.Reason);
                    writer.WriteBooleanValue(row.Refunded);
                    writer.WriteNumberValue(row.AmountMinor);
                    if (fields.Length == 6) writer.WriteNullValue();
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.Flush();
                return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
            }
            foreach (var row in snapshot.Rows)
            {
                writer.WriteStartArray();
                for (var index = 0; index < fields.Length; index++)
                    (snapshot.Positional ? row[index] : row.GetProperty(fields[index])).WriteTo(writer);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}
