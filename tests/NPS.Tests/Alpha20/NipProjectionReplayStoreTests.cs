// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Invocation;
using NPS.NIP.Storage;

namespace NPS.Tests.Alpha20;

/// <summary>Actual file-backed replay checks; this does not qualify multi-host/network storage.</summary>
public sealed class NipProjectionReplayStoreTests : IDisposable
{
    private const string Audience = "service:results", Issuer = "urn:nps:node:fixture:gateway";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nps-context-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _time = new();
    private string FilePath => Path.Combine(_directory, "replay.sqlite");
    public NipProjectionReplayStoreTests() => Directory.CreateDirectory(_directory);
    private Task<SqliteNipProjectionReplayStore> Open(bool initialize = false, int capacity = 2) =>
        SqliteNipProjectionReplayStore.OpenAsync(FilePath, Audience, capacity, _time, initialize);
    private ValueTask<NipProjectionReplayOutcome> Claim(SqliteNipProjectionReplayStore store, string id = "00000000000000000000000000000001") =>
        store.ClaimAsync(Audience, Issuer, id, _time.Now + 30, _time.Now + 35);

    [Fact]
    public async Task SameFileSurvivesReopenAndCannotBeInitializedAgain()
    {
        var store = await Open(initialize: true);
        Assert.Equal(NipProjectionReplayOutcome.Claimed, await Claim(store));
        var reopened = await Open();
        Assert.Equal(NipProjectionReplayOutcome.Duplicate, await Claim(reopened));
        await Unavailable(async () => _ = await Open(initialize: true));
        Assert.Equal(NipProjectionReplayOutcome.Duplicate, await Claim(await Open()));
    }

    [Fact]
    public async Task ConcurrentIndependentConnectionsAtomicallyAdmitAtMostOne()
    {
        var one = await Open(initialize: true);
        var two = await Open();
        var outcomes = await Task.WhenAll(Task.Run(async () => await Claim(one)), Task.Run(async () => await Claim(two)));
        Assert.Single(outcomes, value => value == NipProjectionReplayOutcome.Claimed);
        Assert.Single(outcomes, value => value == NipProjectionReplayOutcome.Duplicate);
    }

    [Fact]
    public async Task CapacityNeverEvictsLiveReplayButExpiryReleasesTheSlot()
    {
        var store = await Open(initialize: true, capacity: 1);
        await Claim(store);
        await Unavailable(async () => _ = await Claim(store, "00000000000000000000000000000002"));
        Assert.Equal(NipProjectionReplayOutcome.Duplicate, await Claim(store));
        _time.Seconds = 35;
        Assert.Equal(NipProjectionReplayOutcome.Claimed, await Claim(store, "00000000000000000000000000000002"));
    }

    [Fact]
    public async Task MissingOrLostStoreNeverSilentlyCreatesEmptyReplayState()
    {
        await Unavailable(async () => _ = await Open());
        Assert.False(File.Exists(FilePath));
        var store = await Open(initialize: true);
        await Claim(store);
        File.Delete(FilePath);
        await Unavailable(async () => _ = await Claim(store));
        await Unavailable(async () => _ = await Open());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AudienceCapacityAndCorruptStorageFailClosedWithoutDetails()
    {
        await Open(initialize: true);
        await Unavailable(async () => _ = await SqliteNipProjectionReplayStore.OpenAsync(FilePath, "other", 2, _time));
        await Unavailable(async () => _ = await Open(capacity: 3));
        File.WriteAllText(FilePath, "not a sqlite database");
        var error = await Assert.ThrowsAsync<NipInvocationException>(async () => await Open());
        Assert.Equal("NIP-CONTEXT-VERIFIER-UNAVAILABLE", error.Code);
        Assert.DoesNotContain(_directory, error.Message);
    }

    [Fact]
    public async Task ExpiredAdmissionDoesNotClaimAndClockRollbackFailsClosed()
    {
        var store = await Open(initialize: true);
        var expired = await store.ClaimAsync(Audience, Issuer, "00000000000000000000000000000001", _time.Now, _time.Now + 5);
        Assert.Equal(NipProjectionReplayOutcome.Expired, expired);
        Assert.Equal(NipProjectionReplayOutcome.Claimed, await Claim(store));
        _time.Seconds = -1;
        await Unavailable(async () => _ = await Claim(store));
    }

    [Fact]
    public async Task UnboundedRetentionAndWrongAudienceNeverInsert()
    {
        var store = await Open(initialize: true);
        await Unavailable(async () => _ = await store.ClaimAsync("other", Issuer,
            "00000000000000000000000000000001", _time.Now + 30, _time.Now + 35));
        await Unavailable(async () => _ = await store.ClaimAsync(Audience, Issuer,
            "00000000000000000000000000000001", _time.Now + 300, _time.Now + 305));
        Assert.Equal(NipProjectionReplayOutcome.Claimed, await Claim(store));
    }

    [Fact]
    public async Task FirstInstallationCreatesPrivateFileWithoutOverwritingPermissions()
    {
        await Open(initialize: true);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        await Open();
    }

    private static async Task Unavailable(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<NipInvocationException>(action);
        Assert.Equal("NIP-CONTEXT-VERIFIER-UNAVAILABLE", error.Code);
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private sealed class Clock : TimeProvider
    {
        public long Seconds { get; set; }
        public long Now => 1_800_000_000 + Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }
}
