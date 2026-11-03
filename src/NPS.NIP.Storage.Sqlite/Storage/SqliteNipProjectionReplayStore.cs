// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using NPS.NIP.Invocation;

namespace NPS.NIP.Storage;

/// <summary>
/// Durable bounded projection replay on one host. Every replica for this audience MUST
/// open the same dedicated local file; independent files/network filesystems/multi-host
/// failover are not supported. Uses existing SQLite dependency, WAL/FULL and write locking.
/// No default deployment or fallback in-memory store is registered.
/// </summary>
public sealed class SqliteNipProjectionReplayStore : INipProjectionReplayStore
{
    private readonly string _connectionString, _audience;
    private readonly int _capacity;
    private readonly TimeProvider _time;

    private SqliteNipProjectionReplayStore(string file, string audience, int capacity, TimeProvider time)
    {
        _connectionString = new SqliteConnectionStringBuilder
            { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString();
        _audience = audience;
        _capacity = capacity;
        _time = time;
    }

    /// <summary>
    /// Open a dedicated absolute application-owned file. Audience/capacity/schema
    /// identity persists and cannot silently change on restart. Parent directory must exist.
    /// Missing files fail closed by default, so losing replay state cannot silently reset it.
    /// Explicit initializeNewFile is only for first installation; it atomically requires a
    /// nonexistent file, creates private storage and never replaces existing permissions/data.
    /// </summary>
    public static async Task<SqliteNipProjectionReplayStore> OpenAsync(string file, string audience,
        int capacity = 65536, TimeProvider? timeProvider = null, bool initializeNewFile = false,
        CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(file) || string.IsNullOrWhiteSpace(audience)
            || System.Text.Encoding.UTF8.GetByteCount(audience) > 512 || capacity is < 1 or > 1_000_000)
            throw Unavailable();
        try
        {
            if (initializeNewFile)
            {
                var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var newFile = new FileStream(file, create);
            }
            else if (!File.Exists(file)) throw Unavailable();
            var store = new SqliteNipProjectionReplayStore(file, audience, capacity, timeProvider ?? TimeProvider.System);
            await using var connection = new SqliteConnection(store._connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            if (initializeNewFile)
            {
                await Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS nps_projection_meta (
                    id INTEGER PRIMARY KEY CHECK(id=1), schema_version INTEGER NOT NULL,
                    audience TEXT NOT NULL, capacity INTEGER NOT NULL, last_now INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS nps_projection_replay (
                    issuer TEXT NOT NULL, projection_id TEXT NOT NULL, retain_until INTEGER NOT NULL,
                    PRIMARY KEY(issuer,projection_id));
                CREATE INDEX IF NOT EXISTS nps_projection_expiry ON nps_projection_replay(retain_until);
                """, ct).ConfigureAwait(false);
                await Execute(connection, transaction,
                    "INSERT INTO nps_projection_meta VALUES(1,1,$audience,$capacity,0)", ct,
                    ("$audience", audience), ("$capacity", capacity)).ConfigureAwait(false);
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT schema_version,audience,capacity FROM nps_projection_meta WHERE id=1";
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) != 1
                    || reader.GetString(1) != audience || reader.GetInt32(2) != capacity) throw Unavailable();
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Unavailable(); }
    }

    /// <inheritdoc />
    public async ValueTask<NipProjectionReplayOutcome> ClaimAsync(string audience, string issuer,
        string projectionId, long acceptBefore, long retainUntil, CancellationToken ct = default)
    {
        if (audience != _audience || string.IsNullOrWhiteSpace(issuer)
            || System.Text.Encoding.UTF8.GetByteCount(issuer) > 512
            || projectionId.Length != 32 || projectionId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || acceptBefore < 0 || retainUntil < acceptBefore || retainUntil > 9_007_199_254_740_991) throw Unavailable();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await Execute(connection, null, "PRAGMA synchronous=FULL;", ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var now = _time.GetUtcNow().ToUnixTimeSeconds(); // AFTER write lock acquisition.
            await using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "SELECT last_now FROM nps_projection_meta WHERE id=1 AND audience=$audience AND capacity=$capacity AND schema_version=1";
            metadata.Parameters.AddWithValue("$audience", _audience);
            metadata.Parameters.AddWithValue("$capacity", _capacity);
            var previous = await metadata.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (previous is not long last || now < last || now < 0 || retainUntil > now + 70) throw Unavailable();
            await Execute(connection, transaction, "UPDATE nps_projection_meta SET last_now=$now WHERE id=1", ct,
                ("$now", now)).ConfigureAwait(false);
            NipProjectionReplayOutcome outcome;
            if (now >= acceptBefore) outcome = NipProjectionReplayOutcome.Expired;
            else
            {
                await Execute(connection, transaction, "DELETE FROM nps_projection_replay WHERE retain_until<=$now", ct,
                    ("$now", now)).ConfigureAwait(false);
                await using var lookup = connection.CreateCommand();
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT 1 FROM nps_projection_replay WHERE issuer=$issuer AND projection_id=$id";
                lookup.Parameters.AddWithValue("$issuer", issuer);
                lookup.Parameters.AddWithValue("$id", projectionId);
                if (await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) outcome = NipProjectionReplayOutcome.Duplicate;
                else
                {
                    await using var count = connection.CreateCommand();
                    count.Transaction = transaction;
                    count.CommandText = "SELECT COUNT(*) FROM nps_projection_replay";
                    if (Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false)) >= _capacity) throw Unavailable();
                    await Execute(connection, transaction,
                        "INSERT INTO nps_projection_replay VALUES($issuer,$id,$until)", ct,
                        ("$issuer", issuer), ("$id", projectionId), ("$until", retainUntil)).ConfigureAwait(false);
                    outcome = NipProjectionReplayOutcome.Claimed;
                }
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Unavailable(); }
    }

    private static async Task Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NipInvocationException Unavailable() => new("NIP-CONTEXT-VERIFIER-UNAVAILABLE");
}
