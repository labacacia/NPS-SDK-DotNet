// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    private const string TrafficBoundary = "TCP-stream bytes including TLS records and HTTP/1.1 framing; frontend plus gateway egress; excludes TCP/IP packets; setup/cleanup separate";
    private const string RuntimeCpuBoundary = "co-host process CPU delta brackets workflow including cold frontend shutdown and counter-read overhead; no protocol attribution";
    private JsonElement? _runtimeExpectedAnswer;
    private object? _runtimeSourceTransition;

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(100000)]
    public async Task PairedRuntimeEvidenceHasEqualOracleAndModelBoundary(int scale)
        => await CapturePairedRuntimeEvidence(scale);

    private async Task CapturePairedRuntimeEvidence(int scale, bool changeMatrix = false, int updatePercent = 0)
    {
        _oracleScale = scale;
        _toModel = true; // Trusted operator opt-in for this preregistered synthetic fixture only.
        var authorizedFields = new[] { "id", "period", "reason", "refunded", "amount_minor" }
            .Concat(_oracleAddedNullableField ? new[] { "note" } : []).ToArray();
        _policy.SetSourceAuthority(Authority() with { Fields = authorizedFields, AllowModelEgress = true });
        var output = Environment.GetEnvironmentVariable(changeMatrix ? "NPS_DATA_WORKFLOW_CHANGE_CAPTURE_DIR" : "NPS_DATA_WORKFLOW_CAPTURE_DIR");
        var label = changeMatrix ? $"changes-{scale}-{(_oracleAddedNullableField ? "nullable" : "none")}-{updatePercent}" : scale.ToString();
        var repeat = output is null ? 1 : FrozenMethod().GetProperty("transport_repeats").GetInt32();
        var warmups = output is null ? 0 : FrozenMethod().GetProperty("transport_warmups").GetInt32();
        using var setup = PublicClient();
        var origin = await setup.CreateAsync("orders", Guid.NewGuid(), ttlSeconds: 900);
        var originPage = await setup.ReadAsync(origin, 100, authorizedFields);
        var sourceRows = originPage.Rows.Where(row => row.GetProperty("period").GetString() == "current"
            && row.GetProperty("refunded").GetBoolean()).Take(5).ToArray();
        Assert.Equal(5, sourceRows.Length);
        var full = OracleCapture(JsonSerializer.SerializeToElement(new { source_id = "orders" })).Rows;
        var fixtureHash = HashRuntimeFixtureRows(full);
        var records = new List<object>();
        var violations = new List<string>();
        foreach (var compression in new[] { "identity", "gzip-response" })
        {
            _gzipResponses = compression == "gzip-response";
            foreach (var warm in new[] { false, true })
            {
                string? pairedVisible = null;
                // Alternate first arm at the next cache/compression combination.
                var arms = warm ? new[] { true, false } : new[] { false, true };
                foreach (var nps in arms)
                {
                    var front = new TlsTraffic();
                    _downstreamTraffic = new TlsTraffic();
                    var elapsed = new List<double>();
                    var cpuMilliseconds = new List<double>();
                    var traffic = new List<long>();
                    var resident = new List<long>();
                    var gzipResponses = new List<int>();
                    var managed = new List<long>();
                    var threads = new List<int>();
                    JsonElement answer = default;
                    string? visible = null;
                    using var persistent = warm ? RuntimePath(nps, front) : null;
                    for (var index = -warmups; index < repeat; index++)
                    {
                        using var cold = warm ? null : RuntimePath(nps, front);
                        var path = persistent ?? cold!;
                        var before = front.Total + _downstreamTraffic.Total;
                        var gzipBefore = _gzipResponseCount;
                        using var process = Process.GetCurrentProcess();
                        var cpuBefore = process.TotalProcessorTime;
                        var started = Stopwatch.GetTimestamp();
                        JsonElement observed;
                        try { observed = await ExecuteMeasuredWorkflow(path, origin); }
                        catch (ResultHttpException error)
                        {
                            if (output is not null)
                            {
                                using var failed = Process.GetCurrentProcess();
                                await File.WriteAllTextAsync(Path.Combine(output, $"runtime-failure-{label}.json"), JsonSerializer.Serialize(new
                                {
                                    rows = scale, nps, warm, compression, iteration = index,
                                    http_status = error.HttpStatus, nps_status = error.NpsStatus, error_code = error.ErrorCode,
                                    operation = path.LastOperation, server_exception_types = _runtimeExceptionTypes.ToArray(),
                                    managed_bytes = GC.GetTotalMemory(false), working_set_bytes = failed.WorkingSet64,
                                    peak_rss_bytes = failed.PeakWorkingSet64, thread_count = ThreadPool.ThreadCount,
                                }, new JsonSerializerOptions { WriteIndented = true }));
                            }
                            throw new Xunit.Sdk.XunitException($"Runtime operation failed: HTTP {error.HttpStatus}; {error.ErrorCode}.");
                        }
                        cold?.Dispose(); // Include cold frontend shutdown, not just response payload.
                        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        process.Refresh();
                        var cpuDuration = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                        var bytes = front.Total + _downstreamTraffic.Total - before;
                        answer = observed;
                        visible = JsonSerializer.Serialize(new
                        {
                            computed = observed, source_records = sourceRows,
                            citations = sourceRows.Select(row => new { result_ref = origin.ResultRef,
                                snapshot_id = origin.SnapshotId, record_id = row.GetProperty("id").GetString() }),
                        });
                        Assert.True(Encoding.UTF8.GetByteCount(visible) <= 32768);
                        Assert.True(duration <= 120000);
                        Assert.True(cpuDuration >= 0);
                        Assert.True(bytes > 0);
                        Assert.Equal(_gzipResponses, _gzipResponseCount > gzipBefore);
                        if (index < 0) continue;
                        elapsed.Add(duration);
                        cpuMilliseconds.Add(cpuDuration);
                        traffic.Add(bytes);
                        resident.Add(process.WorkingSet64);
                        managed.Add(GC.GetTotalMemory(forceFullCollection: false));
                        threads.Add(ThreadPool.ThreadCount);
                        gzipResponses.Add(_gzipResponseCount - gzipBefore);
                    }
                    Assert.NotNull(visible);
                    if (pairedVisible is null) pairedVisible = visible;
                    else Assert.Equal(pairedVisible, visible);
                    using var current = Process.GetCurrentProcess();
                    var withinMemory = current.PeakWorkingSet64 <= 536870912;
                    if (output is not null && !withinMemory) violations.Add("rss-budget");
                    records.Add(new
                    {
                        arm = nps ? "D-nps-runtime" : "C-http-runtime", rows = scale,
                        schema_churn = _oracleAddedNullableField ? "add-nullable-field" : "none",
                        updated_row_fraction = updatePercent / 100m, source_transition = _runtimeSourceTransition,
                        fixture_sha256 = fixtureHash, answer, model_visible_utf8 = visible,
                        cache_state = warm ? "warm" : "cold", compression,
                        source = new
                        {
                            revision = Environment.GetEnvironmentVariable("NPS_DATA_WORKFLOW_REVISION") ?? "working-tree-correctness-only",
                            binding = nps ? "HTTPS NWP Action/CapsFrame + fresh NIP gateway projection" : "ordinary HTTPS JSON tools",
                            boundary = TrafficBoundary, request_id = Guid.NewGuid().ToString("D"),
                            environment = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + "; "
                                + System.Runtime.InteropServices.RuntimeInformation.OSDescription + "; "
                                + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + "; cpus=" + Environment.ProcessorCount
                                + "; server_gc=" + System.Runtime.GCSettings.IsServerGC + "; loopback provisioned mTLS",
                        },
                        wire_bytes = traffic.Sum(), wire_bytes_per_workflow = traffic,
                        observed_gzip_responses_per_workflow = gzipResponses,
                        latency_ms = elapsed, rss_bytes_after_workflow = resident, process_peak_rss_bytes = current.PeakWorkingSet64,
                        process_cpu_ms_per_workflow = cpuMilliseconds, cpu_measurement_boundary = RuntimeCpuBoundary,
                        managed_bytes_after_workflow = managed, thread_pool_threads_after_workflow = threads,
                        gc_total_available_memory_bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                        rss_budget_passed = withinMemory,
                        provider_usage = (object?)null, warmups, repeats = repeat,
                        model_egress_policy = "operator-enabled preregistered synthetic evidence only; both paths enforce store ToModel checks",
#if DEBUG
                        build_configuration = "Debug-correctness-only",
#else
                        build_configuration = "Release",
#endif
                        cache_definition = "frontend connection new/reused; TLS resumption disabled; post-transition source immutable; no schema/provider cache claim",
                        topology = nps ? "public gateway and receiver; downstream client currently per request" : "direct ordinary endpoint; same source/store/policy operators",
                        setup = "shared live provenance result + first source page prepared before measurement; separate cleanup after arms",
                        claim = "HTTP reference workflow only; no native NCP/Arrow/Flight/Protobuf efficiency claim",
                    });
                    if (output is not null) await File.WriteAllTextAsync(Path.Combine(output, $"runtime-progress-{label}.json"),
                        JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                    _downstreamTraffic = null;
                }
            }
        }
        await setup.ReleaseAsync(origin);
        if (output is not null)
        {
            Assert.True(Path.IsPathFullyQualified(output));
            Directory.CreateDirectory(output);
            var path = Path.Combine(output, $"runtime-{label}.json");
            Assert.False(File.Exists(path), "Do not overwrite an earlier runtime observation.");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Assert.Empty(violations); // Failed budget observations remain on disk for review.
        }
    }

    private async Task<JsonElement> ExecuteMeasuredWorkflow(RuntimeResultPath path, ResultDescriptor origin)
    {
        var all = await path.Create();
        Assert.Equal(origin.SnapshotId, all.SnapshotId); // Same immutable source/version, independently captured.
        var orders = await path.Aggregate(all, sum: false);
        var refunds = await path.Create(refunded: true);
        var totals = await path.Aggregate(refunds, sum: true);
        var page = await path.Read(refunds);
        var groups = new Dictionary<string, object>();
        var anomalies = new List<string>();
        foreach (var reason in Reasons)
        {
            (long Count, long Refunds, long Amount) Period(string period)
            {
                var order = orders.Groups.Single(row => row.GetProperty("reason").GetString() == reason && row.GetProperty("period").GetString() == period);
                var refund = totals.Groups.Where(row => row.GetProperty("reason").GetString() == reason && row.GetProperty("period").GetString() == period).ToArray();
                return (order.GetProperty("count").GetInt64(), refund.Length == 0 ? 0 : refund[0].GetProperty("count").GetInt64(),
                    refund.Length == 0 ? 0 : refund[0].GetProperty("sum").GetInt64());
            }
            var baseline = Period("baseline");
            var current = Period("current");
            var ids = page.Rows.Where(row => row.GetProperty("period").GetString() == "current"
                && row.GetProperty("reason").GetString() == reason).Take(5).Select(row => row.GetProperty("id").GetString()).ToArray();
            groups.Add(reason, new
            {
                baseline = new { orders = baseline.Count, refunds = baseline.Refunds, refund_amount_minor = baseline.Amount },
                current = new { orders = current.Count, refunds = current.Refunds, refund_amount_minor = current.Amount },
                sample_current_refund_ids = ids,
            });
            if (Math.Min(baseline.Count, current.Count) >= 10 && current.Refunds >= 3
                && checked(10 * (current.Refunds * baseline.Count - baseline.Refunds * current.Count)) >= current.Count * baseline.Count)
                anomalies.Add(reason);
        }
        var answer = JsonSerializer.SerializeToElement(new { groups, anomaly_reasons = anomalies });
        Assert.True(JsonElement.DeepEquals(_runtimeExpectedAnswer ?? FrozenMethod().GetProperty("reference_answers").GetProperty(_oracleScale.ToString()), answer));
        await path.Release(refunds);
        await path.Release(all);
        return answer;
    }
}
