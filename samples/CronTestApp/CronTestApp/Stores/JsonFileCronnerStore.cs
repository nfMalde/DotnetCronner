using System.Collections.Concurrent;
using System.Text.Json;
using CronTestApp.Configuration;
using CronTestApp.Services;
using DotnetCronner;

namespace CronTestApp.Stores;

/// <summary>
/// A hand-written <see cref="ICronnerStore"/> — the "bring your own persistence" path from
/// <c>docs/custom-store.md</c>, kept deliberately small: tasks live in a JSON file, guarded by a
/// semaphore. Selected with <c>CRONNER_STORE=custom</c>, which wires it up via <c>UseStore&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// The store is effectively a singleton, so it takes no scoped dependency (a real one would inject
/// <c>IServiceScopeFactory</c> and open a scope per operation). Its claim is atomic only within this
/// process, which is all a single-instance scheduler needs.
/// </remarks>
public sealed class JsonFileCronnerStore : ICronnerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, RunSession> _sessions = new();
    // Execution history, keyed by each run's correlation id. Kept in memory to keep the sample small; a
    // durable store would persist these next to the jobs (the EF Core and Redis stores do).
    private readonly ConcurrentDictionary<string, CronnerJobExecution> _executions = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ILogger<JsonFileCronnerStore> _logger;
    private readonly JobActivityLog _activity;
    private Dictionary<string, CronnerJob>? _jobs;

    /// <summary>Creates the store over the file configured by <c>CRONNER_CUSTOM_STORE_FILE</c>.</summary>
    public JsonFileCronnerStore(TestAppOptions options, JobActivityLog activity, ILogger<JsonFileCronnerStore> logger)
    {
        _path = Path.GetFullPath(options.CustomStoreFile);
        _activity = activity;
        _logger = logger;
        logger.LogInformation("Custom JSON store persisting to {Path}", _path);
    }

    /// <inheritdoc />
    public async Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return jobs.TryGetValue(id, out var job) ? job.Clone() : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return jobs.Values
                .Where(job => state is null || job.State == state)
                .OrderBy(job => job.CreatedUtc)
                .ThenBy(job => job.Id, StringComparer.Ordinal)
                .Skip(Math.Max(0, offset))
                .Take(Math.Max(0, limit))
                .Select(job => job.Clone())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            jobs[job.Id] = job.Clone();
            await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);

            // Attribute the write to this run's session, if one is open (see OnStartAsync/OnCloseAsync).
            if (_sessions.TryGetValue(job.Id, out var session))
                session.CountWrite();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (jobs.Remove(id))
                await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);

            // Cascade: a removed job takes its execution history with it.
            foreach (var execution in _executions.Values.Where(e => e.JobId == id).ToArray())
                _executions.TryRemove(execution.Id, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);

            var due = jobs.Values
                .Where(job => IsEligible(job, now))
                .OrderByDescending(job => job.Priority)
                .ThenBy(job => job.NextRunUtc)
                .Take(Math.Max(0, max))
                .ToArray();

            if (due.Length == 0)
                return [];

            var claimed = new List<CronnerJob>(due.Length);
            foreach (var job in due)
            {
                job.State = CronnerTaskState.Queued;
                job.LockOwner = owner;
                job.LockedUntilUtc = now + lockTtl;
                job.UpdatedUtc = DateTimeOffset.UtcNow;
                claimed.Add(job.Clone());
            }

            await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The keepalive. Extends the lock <em>only</em> while this owner still holds it — returning
    /// <c>false</c> is what tells the scheduler it lost the claim and must cancel its own run.
    /// </remarks>
    public async Task<bool> RenewLockAsync(
        string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!jobs.TryGetValue(id, out var job) || !string.Equals(job.LockOwner, owner, StringComparison.Ordinal))
            {
                _activity.Record(id, $"[store] lock renewal REFUSED — owner is '{job?.LockOwner ?? "(none)"}', not '{owner}'");
                return false;
            }

            job.LockedUntilUtc = lockedUntil;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>One-off retention (the built-in policy): keep only the newest N finished instances of a definition.</remarks>
    public async Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var stale = jobs.Values
                .Where(j => j.Kind == CronnerJobKind.OneOff && j.DefinitionId == definitionId &&
                            j.State is CronnerTaskState.Completed or CronnerTaskState.Failed)
                .OrderByDescending(j => j.UpdatedUtc)
                .Skip(Math.Max(0, keepNewest))
                .Select(j => j.Id)
                .ToArray();

            if (stale.Length == 0)
                return;

            foreach (var id in stale)
                jobs.Remove(id);
            await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);
            _activity.Record(definitionId, $"[store] pruned {stale.Length} old one-off instance(s), keeping newest {keepNewest}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Targeted progress write — leaves lock/schedule fields untouched so it never races the keepalive.</remarks>
    public async Task UpdateProgressAsync(string id, decimal progress, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!jobs.TryGetValue(id, out var job))
                return;

            job.Progress = progress;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Inserts a Running history record at the start of a run (finalized by RecordExecutionFinishedAsync).</remarks>
    public Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default)
    {
        _executions[execution.Id] = execution.Clone();
        _activity.Record(execution.JobId, $"[store] execution {execution.Id[..8]} started (attempt {execution.Attempt})");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Finalizes the record inserted at start (matched on the correlation id) with the terminal status.</remarks>
    public Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default)
    {
        _executions[execution.Id] = execution.Clone();
        _activity.Record(execution.JobId, $"[store] execution {execution.Id[..8]} finished: {execution.Status}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Newest-first history for a task, as surfaced by GET /tasks/{id}/history.</remarks>
    public Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(
        string jobId, int limit, CancellationToken cancellationToken = default)
    {
        var page = _executions.Values
            .Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .Select(e => e.Clone())
            .ToArray();

        return Task.FromResult<IReadOnlyList<CronnerJobExecution>>(page);
    }

    /// <inheritdoc />
    /// <remarks>History retention: keep only the newest N runs per task, pruned after each run finishes.</remarks>
    public Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken cancellationToken = default)
    {
        var stale = _executions.Values
            .Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, keepNewest))
            .ToArray();
        foreach (var execution in stale)
            _executions.TryRemove(execution.Id, out _);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Opens this run's "session" — a real store would open a unit of work / transaction here.</remarks>
    public Task OnStartAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        var session = new RunSession(Guid.NewGuid().ToString("N")[..8]);
        _sessions[job.Id] = session;
        _activity.Record(job.Id, $"[store] session {session.Id} opened (ICronnerStore.OnStartAsync)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Always runs, even when the task failed — a real store would commit or roll back here.</remarks>
    public Task OnCloseAsync(CronnerJob job, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(job.Id, out var session))
        {
            var outcome = job.State is CronnerTaskState.Failed ? "rolled back" : "committed";
            _activity.Record(
                job.Id,
                $"[store] session {session.Id} {outcome} after {session.Writes} write(s), state={job.State} (ICronnerStore.OnCloseAsync)");
        }

        return Task.CompletedTask;
    }

    /// <summary>A stand-in for the unit of work a real store would open per run.</summary>
    private sealed class RunSession(string id)
    {
        public string Id { get; } = id;

        public int Writes => Volatile.Read(ref _writes);

        private int _writes;

        public void CountWrite() => Interlocked.Increment(ref _writes);
    }

    private static bool IsEligible(CronnerJob job, DateTimeOffset now) =>
        job.State != CronnerTaskState.Cancelled &&
        job.NextRunUtc is { } next && next <= now &&
        (job.LockOwner is null || job.LockedUntilUtc is null || job.LockedUntilUtc < now);

    private async Task<Dictionary<string, CronnerJob>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_jobs is not null)
            return _jobs;

        if (!File.Exists(_path))
            return _jobs = [];

        try
        {
            await using var stream = File.OpenRead(_path);
            var jobs = await JsonSerializer
                .DeserializeAsync<List<CronnerJob>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return _jobs = jobs?.ToDictionary(job => job.Id, StringComparer.Ordinal) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable custom store file {Path}; starting from scratch.", _path);
            return _jobs = [];
        }
    }

    private async Task SaveAsync(Dictionary<string, CronnerJob> jobs, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(_path);
        await JsonSerializer
            .SerializeAsync(stream, jobs.Values.ToList(), SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
