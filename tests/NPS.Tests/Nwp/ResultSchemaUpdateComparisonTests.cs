// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    public static IEnumerable<object[]> SchemaUpdateCases() =>
        from scale in new[] { 100, 1000, 100000 }
        from nullable in new[] { false, true }
        from percent in new[] { 0, 1, 10 }
        select new object[] { scale, nullable, percent };

    [Theory]
    [MemberData(nameof(SchemaUpdateCases))]
    public async Task SchemaUpdateRuntimePreservesOldSnapshotsAndMatchesChangedOracle(int scale, bool nullable, int percent)
    {
        _oracleScale = scale;
        _policy.SetSourceAuthority(Authority() with { Fields = ["id", "period", "reason", "refunded", "amount_minor"] });
        using var client = PublicClient();
        var old = await client.CreateAsync("orders", Guid.NewGuid(), ttlSeconds: 900);
        var oldPage = await client.ReadAsync(old, 1, ["id", "amount_minor"]);
        Assert.NotNull(oldPage.NextCursor);
        _oracleUpdatedRows = scale * percent / 100;
        _oracleAddedNullableField = nullable;
        _policy.SetSourceAuthority(Authority() with
        {
            Fields = new[] { "id", "period", "reason", "refunded", "amount_minor" }.Concat(nullable ? new[] { "note" } : []).ToArray(),
            AllowModelEgress = true,
        });
        var fresh = await client.CreateAsync("orders", Guid.NewGuid(), ttlSeconds: 900);
        Assert.Equal(nullable, old.SchemaRef != fresh.SchemaRef);
        Assert.Equal(nullable || percent > 0, old.SnapshotId != fresh.SnapshotId);
        var unchanged = await client.ReadAsync(old, 1, ["id", "amount_minor"]);
        Assert.Equal(oldPage.Rows[0].GetRawText(), unchanged.Rows[0].GetRawText());
        var nextOld = await client.ReadAsync(old, 1, ["id", "amount_minor"], oldPage.NextCursor);
        Assert.Equal("order:000001", nextOld.Rows[0].GetProperty("id").GetString());
        var mismatch = await Assert.ThrowsAsync<ResultHttpException>(() => client.ReadAsync(fresh, 1,
            ["id", "amount_minor"], oldPage.NextCursor));
        Assert.Equal("NWP-RESULT-SNAPSHOT-MISMATCH", mismatch.ErrorCode);
        var first = await client.ReadAsync(fresh, 1, nullable ? ["id", "amount_minor", "note"] : ["id", "amount_minor"]);
        Assert.Equal(1000 + (percent > 0 ? 100 : 0), first.Rows[0].GetProperty("amount_minor").GetInt32());
        if (nullable) Assert.Equal(JsonValueKind.Null, first.Rows[0].GetProperty("note").ValueKind);
        _runtimeExpectedAnswer = ChangedOracle(scale, _oracleUpdatedRows);
        _runtimeSourceTransition = new
        {
            old_schema_ref = old.SchemaRef, new_schema_ref = fresh.SchemaRef,
            old_snapshot_id = old.SnapshotId, new_snapshot_id = fresh.SnapshotId,
            updated_rows = _oracleUpdatedRows, old_page_unchanged = true, old_cursor_continued = true,
            cross_result_cursor_rejected = true,
            mutation_rule = "first N stable record IDs: amount_minor += 100; nullable note=null on every row when selected",
            timing_scope = "transition and retained-old-snapshot checks outside timed post-transition workflows",
        };
        await client.ReleaseAsync(fresh);
        await CapturePairedRuntimeEvidence(scale, changeMatrix: true, updatePercent: percent);
        var after = await client.ReadAsync(old, 1, ["id", "amount_minor"]);
        Assert.Equal(oldPage.Rows[0].GetRawText(), after.Rows[0].GetRawText());
        await client.ReleaseAsync(old);
    }

    private static JsonElement ChangedOracle(int scale, int changedRows)
    {
        var expected = JsonNode.Parse(FrozenMethod().GetProperty("reference_answers").GetProperty(scale.ToString()).GetRawText())!;
        // Independent reference arithmetic: exact counts/IDs do not change; only
        // refunded amounts for the first N stable rows increase by 100 minor units.
        for (var index = 0; index < changedRows; index++)
        {
            var reason = Reasons[index % 4];
            var period = index % 100 < 50 ? "baseline" : "current";
            var refunded = index % 100 < 10 || period == "current" && reason == "delivery" && index % 100 < 80;
            if (!refunded) continue;
            var group = expected["groups"]![reason]![period]!;
            group["refund_amount_minor"] = group["refund_amount_minor"]!.GetValue<long>() + 100;
        }
        return JsonSerializer.SerializeToElement(expected);
    }
}
