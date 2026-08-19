using System.Data;
using DotnetCronner;
using Microsoft.Data.Sqlite;

namespace CronTestApp.Stores;

/// <summary>
/// A store that holds a **scoped, non-concurrent** resource — one open <see cref="SqliteConnection"/>
/// per instance, with no internal locking. Registered with
/// <c>UseStore&lt;ScopedSqliteCronnerStore&gt;(CronnerStoreLifetime.Scoped)</c>.
///
/// <para>This exists to exercise the store lifetime end to end. Under
/// <see cref="CronnerStoreLifetime.Singleton"/> the scheduler would share ONE of these across
/// overlapping operations — the poll loop claiming due tasks while running jobs upsert state — and
/// SQLite would raise a concurrent-access error, the same shape as Npgsql's "a command is already in
/// progress" or EF Core's "a second operation was started on this context instance".</para>
///
/// <para>Under <c>Scoped</c> each operation gets its own instance and its own connection, so the
/// same workload runs cleanly. Flipping <c>CRONNER_STORE</c> between <c>scoped</c> and
/// <c>scoped-broken</c> shows both sides of the fix.</para>
///
/// <para>Deliberately naive: no retry, no pooling, no lock. A production store would be either
/// stateless (hold a factory) or declared <c>Scoped</c> like this one.</para>
/// </summary>
public sealed class ScopedSqliteCronnerStore : ICronnerStore, IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens this instance's own connection. One per scope when registered as Scoped.</summary>
    public ScopedSqliteCronnerStore(IConfiguration configuration)
    {
        var path = configuration["Cronner:ScopedStorePath"] ?? "cronner-scoped.db";
        _connection = new SqliteConnection($"Data Source={path};Cache=Shared");
        _connection.Open();

        using var create = _connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS jobs (
                id             TEXT PRIMARY KEY,
                name           TEXT,
                cron           TEXT,
                state          INTEGER NOT NULL,
                priority       INTEGER NOT NULL,
                next_run_utc   TEXT,
                last_run_utc   TEXT,
                run_count      INTEGER NOT NULL DEFAULT 0,
                retry_count    INTEGER NOT NULL DEFAULT 0,
                last_error     TEXT,
                lock_owner     TEXT,
                locked_until   TEXT,
                created_utc    TEXT NOT NULL,
                updated_utc    TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    public async Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = state is null
            ? "SELECT * FROM jobs ORDER BY created_utc LIMIT $limit OFFSET $offset;"
            : "SELECT * FROM jobs WHERE state = $state ORDER BY created_utc LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        if (state is not null) cmd.Parameters.AddWithValue("$state", (int)state);

        var results = new List<CronnerJob>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Read(reader));
        return results;
    }

    /// <summary>
    /// Insert-or-update. The UPDATE branch deliberately leaves <c>lock_owner</c>/<c>locked_until</c> alone: the
    /// lock is owned by <see cref="AcquireDueAsync"/>, <see cref="RenewLockAsync"/> and <see cref="ReleaseLockAsync"/>,
    /// so a caller writing a stale snapshot can never clear or shorten a claim another instance took in between.
    /// </summary>
    public async Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO jobs (id, name, cron, state, priority, next_run_utc, last_run_utc, run_count,
                              retry_count, last_error, lock_owner, locked_until, created_utc, updated_utc)
            VALUES ($id, $name, $cron, $state, $priority, $next, $last, $runs, $retries, $error,
                    $owner, $until, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = $name, cron = $cron, state = $state, priority = $priority,
                next_run_utc = $next, last_run_utc = $last, run_count = $runs,
                retry_count = $retries, last_error = $error, updated_utc = $updated;
            """;
        Bind(cmd, job);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Claims due tasks. Single-statement so SQLite applies it atomically — the same requirement any
    /// real store has, just expressed without <c>FOR UPDATE SKIP LOCKED</c>.
    /// </summary>
    public async Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        var nowText = Text(now);
        var untilText = Text(now + lockTtl);

        await using var claim = _connection.CreateCommand();
        claim.CommandText =
            """
            UPDATE jobs
               SET lock_owner = $owner, locked_until = $until, state = $queued, updated_utc = $now
             WHERE id IN (
                   SELECT id FROM jobs
                    WHERE state <> $cancelled
                      AND next_run_utc IS NOT NULL
                      AND next_run_utc <= $now
                      AND (lock_owner IS NULL OR locked_until IS NULL OR locked_until < $now)
                    ORDER BY priority DESC, next_run_utc
                    LIMIT $max
             );
            """;
        claim.Parameters.AddWithValue("$owner", owner);
        claim.Parameters.AddWithValue("$until", untilText);
        claim.Parameters.AddWithValue("$now", nowText);
        claim.Parameters.AddWithValue("$max", max);
        claim.Parameters.AddWithValue("$queued", (int)CronnerTaskState.Queued);
        claim.Parameters.AddWithValue("$cancelled", (int)CronnerTaskState.Cancelled);
        await claim.ExecuteNonQueryAsync(cancellationToken);

        await using var fetch = _connection.CreateCommand();
        fetch.CommandText = "SELECT * FROM jobs WHERE lock_owner = $owner AND locked_until = $until;";
        fetch.Parameters.AddWithValue("$owner", owner);
        fetch.Parameters.AddWithValue("$until", untilText);

        var claimed = new List<CronnerJob>();
        await using var reader = await fetch.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) claimed.Add(Read(reader));
        return claimed;
    }

    public async Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        // Owner-conditional, and never for a Cancelled task (a cancel from another instance must stop the run).
        cmd.CommandText =
            "UPDATE jobs SET locked_until = $until, updated_utc = $now WHERE id = $id AND lock_owner = $owner AND state <> $cancelled;";
        cmd.Parameters.AddWithValue("$until", Text(lockedUntil));
        cmd.Parameters.AddWithValue("$now", Text(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$cancelled", (int)CronnerTaskState.Cancelled);

        // Zero rows means the lock moved on (or the task was cancelled) — the engine stops the run. If the
        // database were unreachable this would THROW instead, and the engine keeps the run alive only while the
        // last confirmed expiry is still ahead.
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Owner-conditional release: a former owner cannot free a lock someone else holds now.</summary>
    public async Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken cancellationToken = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE jobs SET lock_owner = NULL, locked_until = NULL, updated_utc = $now WHERE id = $id AND lock_owner = $owner;";
        cmd.Parameters.AddWithValue("$now", Text(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$owner", owner);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // This store keeps no execution history (the JSON store and the shipped stores do), so the history-related
    // members keep their default no-op implementations — including FinalizeOrphanedExecutionsAsync.

    public void Dispose() => _connection.Dispose();

    private static string Text(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static DateTimeOffset? Time(IDataRecord row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(row.GetString(ordinal)).ToUniversalTime();

    private static string? Str(IDataRecord row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetString(ordinal);

    private static CronnerJob Read(IDataRecord row) => new()
    {
        Id = row.GetString(row.GetOrdinal("id")),
        Name = Str(row, row.GetOrdinal("name")) ?? string.Empty,
        CronExpression = Str(row, row.GetOrdinal("cron")),
        State = (CronnerTaskState)row.GetInt32(row.GetOrdinal("state")),
        Priority = (CronnerTaskPriority)row.GetInt32(row.GetOrdinal("priority")),
        NextRunUtc = Time(row, row.GetOrdinal("next_run_utc")),
        LastRunUtc = Time(row, row.GetOrdinal("last_run_utc")),
        RunCount = row.GetInt32(row.GetOrdinal("run_count")),
        RetryCount = row.GetInt32(row.GetOrdinal("retry_count")),
        LastError = Str(row, row.GetOrdinal("last_error")),
        LockOwner = Str(row, row.GetOrdinal("lock_owner")),
        LockedUntilUtc = Time(row, row.GetOrdinal("locked_until")),
        CreatedUtc = Time(row, row.GetOrdinal("created_utc")) ?? DateTimeOffset.UtcNow,
        UpdatedUtc = Time(row, row.GetOrdinal("updated_utc")) ?? DateTimeOffset.UtcNow,
    };

    private static void Bind(SqliteCommand cmd, CronnerJob job)
    {
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$name", (object?)job.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cron", (object?)job.CronExpression ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", (int)job.State);
        cmd.Parameters.AddWithValue("$priority", (int)job.Priority);
        cmd.Parameters.AddWithValue("$next", job.NextRunUtc is null ? DBNull.Value : Text(job.NextRunUtc.Value));
        cmd.Parameters.AddWithValue("$last", job.LastRunUtc is null ? DBNull.Value : Text(job.LastRunUtc.Value));
        cmd.Parameters.AddWithValue("$runs", job.RunCount);
        cmd.Parameters.AddWithValue("$retries", job.RetryCount);
        cmd.Parameters.AddWithValue("$error", (object?)job.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)job.LockOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$until", job.LockedUntilUtc is null ? DBNull.Value : Text(job.LockedUntilUtc.Value));
        cmd.Parameters.AddWithValue("$created", Text(job.CreatedUtc));
        cmd.Parameters.AddWithValue("$updated", Text(job.UpdatedUtc));
    }
}
