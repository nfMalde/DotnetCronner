namespace DotnetCronner;

/// <summary>
/// The pluggable persistence contract for DotnetCronner. Implement this interface to back the
/// scheduler with any storage technology (a relational database, a document store, Redis, or a
/// bespoke ORM) without being tied to Entity Framework Core or any particular library.
/// </summary>
/// <remarks>
/// <para>Implementations must be safe for concurrent use.</para>
/// <para>
/// <b>The execution lock.</b> <see cref="CronnerJob.LockOwner"/> and <see cref="CronnerJob.LockedUntilUtc"/>
/// are owned <em>exclusively</em> by the three lock primitives — <see cref="AcquireDueAsync"/> (claim),
/// <see cref="RenewLockAsync"/> (keepalive) and <see cref="ReleaseLockAsync"/> (release). Each of them must be
/// atomic and conditional on the current owner, because that is what makes "one run per task across processes"
/// true. <see cref="UpsertAsync"/> must therefore <em>preserve</em> an existing row's lock fields: a writer that
/// read the job a moment ago may hold a stale snapshot, and if its write could clear or shorten a lock that
/// another instance acquired in between, a third instance could claim the task while the first is still running.
/// </para>
/// </remarks>
public interface ICronnerStore
{
    /// <summary>Returns the job with the given <paramref name="id"/>, or <c>null</c> if it does not exist.</summary>
    Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of jobs, optionally filtered by <paramref name="state"/>, ordered by
    /// <see cref="CronnerJob.CreatedUtc"/>.
    /// </summary>
    Task<IReadOnlyList<CronnerJob>> GetAsync(
        CronnerTaskState? state,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the job if it does not exist, otherwise updates it. On an <em>update</em> the store must keep
    /// the row's current <see cref="CronnerJob.LockOwner"/> / <see cref="CronnerJob.LockedUntilUtc"/> and
    /// ignore the values on <paramref name="job"/> — lock state is changed only through
    /// <see cref="AcquireDueAsync"/>, <see cref="RenewLockAsync"/> and <see cref="ReleaseLockAsync"/>. On an
    /// insert the job's lock fields are taken as-is (normally <c>null</c>).
    /// </summary>
    /// <remarks>
    /// A store that still overwrites the lock fields here keeps working with the default
    /// <see cref="ReleaseLockAsync"/>, but carries a narrow race: a stale read followed by this write can clear a
    /// lock another instance just acquired. Implement lock-preserving updates for multi-instance deployments.
    /// </remarks>
    Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default);

    /// <summary>Removes the job with the given <paramref name="id"/>. No-op if it does not exist.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically finds jobs that are due to run (<see cref="CronnerJob.NextRunUtc"/> at or before
    /// <paramref name="now"/>) and whose execution lock is free or expired, claims up to
    /// <paramref name="max"/> of them for <paramref name="owner"/>, and returns the claimed jobs with
    /// their state set to <see cref="CronnerTaskState.Queued"/>.
    /// </summary>
    /// <remarks>
    /// This is the claim that makes "one run per task across processes" true, so it must be race-safe against
    /// other instances calling it at the same moment: the eligibility check (not <see cref="CronnerTaskState.Cancelled"/>,
    /// due, lock free or expired) and the write of the new owner must happen atomically per job — a
    /// conditional <c>UPDATE … WHERE</c> that re-asserts eligibility, <c>SELECT … FOR UPDATE SKIP LOCKED</c>,
    /// <c>SET NX</c> on a lock key, or an optimistic-concurrency token. Reading candidates first and writing
    /// them unconditionally is <em>not</em> safe.
    /// </remarks>
    /// <param name="now">The current time used to evaluate due-ness.</param>
    /// <param name="owner">A token identifying the claiming worker/instance.</param>
    /// <param name="lockTtl">How long the claim remains valid before another worker may reclaim it.</param>
    /// <param name="max">The maximum number of jobs to claim in this call.</param>
    Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
        DateTimeOffset now,
        string owner,
        TimeSpan lockTtl,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the execution lock on the job with the given <paramref name="id"/> to
    /// <paramref name="lockedUntil"/>, but only if it is still held by <paramref name="owner"/>. The
    /// scheduler calls this periodically while a task runs so a long execution keeps its claim and is not
    /// mistaken for a stalled worker and reclaimed.
    /// </summary>
    /// <remarks>
    /// Return <c>false</c> only when the claim is <em>definitively</em> not the caller's any more: the row is
    /// gone, another owner holds it, or the job was set to <see cref="CronnerTaskState.Cancelled"/> (the
    /// scheduler reads that as "stop this run" and cancels it). When the store cannot tell — the database is
    /// unreachable, the connection dropped mid-call — <b>throw</b> instead of returning <c>false</c>. The
    /// scheduler owns the policy for an unconfirmed lease: it keeps the run alive while the last confirmed
    /// expiry is still in the future and abandons it before that expiry can lapse, so a transient blip does not
    /// kill an hour-long job and a real outage cannot produce a second concurrent run.
    /// </remarks>
    /// <param name="id">The job whose lock to extend.</param>
    /// <param name="owner">The worker that must still own the lock for the renewal to apply.</param>
    /// <param name="lockedUntil">The new lock expiry to set.</param>
    /// <returns>
    /// <c>true</c> if the lock was extended; <c>false</c> if the caller no longer owns it (it expired and
    /// was reclaimed, was released, the job was cancelled, or the job is gone). On <c>false</c> the caller
    /// must stop running the task to avoid a second concurrent execution.
    /// </returns>
    Task<bool> RenewLockAsync(
        string id,
        string owner,
        DateTimeOffset lockedUntil,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the execution lock on the job with the given <paramref name="id"/>, but only if it is still
    /// held by <paramref name="owner"/> — a former owner whose claim was reclaimed by another instance must not
    /// be able to free that instance's lock. The scheduler calls this after it has persisted a run's outcome.
    /// </summary>
    /// <remarks>
    /// The default implementation reads the job and, if <paramref name="owner"/> holds it, clears the lock
    /// fields and writes it back through <see cref="UpsertAsync"/>. That only works for a store whose
    /// <see cref="UpsertAsync"/> still overwrites lock fields; a store that preserves them (the recommended
    /// contract) <b>must</b> override this with an atomic owner-conditional update.
    /// </remarks>
    /// <returns><c>true</c> if the lock was held by <paramref name="owner"/> and has been released; otherwise <c>false</c>.</returns>
    async Task<bool> ReleaseLockAsync(string id, string owner, CancellationToken cancellationToken = default)
    {
        var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null || job.LockOwner != owner)
            return false;

        job.LockOwner = null;
        job.LockedUntilUtc = null;
        await UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Called once at the very start of a job run, before any other store call for that run. Use it to
    /// open a per-run session / unit of work / DI scope (each run gets its own). Runs for a given task id
    /// do not overlap by default, so a store may key per-run state by <see cref="CronnerJob.Id"/>. The
    /// default implementation does nothing.
    /// </summary>
    Task OnStartAsync(CronnerJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Called once at the very end of a job run — always, even if the run failed — after every other store
    /// call for that run. Use it to commit/close and dispose whatever <see cref="OnStartAsync"/> opened;
    /// inspect <see cref="CronnerJob.State"/> / <see cref="CronnerJob.LastError"/> to decide commit vs
    /// rollback. The default implementation does nothing.
    /// </summary>
    Task OnCloseAsync(CronnerJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Deletes finished one-off (<see cref="CronnerJobKind.OneOff"/>) rows for the given
    /// <paramref name="definitionId"/>, keeping the newest <paramref name="keepNewest"/> and removing the
    /// rest. The scheduler calls this after a one-off finishes when retention is enabled. The default
    /// implementation does nothing — override it (a targeted delete) to enable retention for a custom store.
    /// </summary>
    Task PruneCompletedOneOffsAsync(string definitionId, int keepNewest, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Updates <em>only</em> the <see cref="CronnerJob.Progress"/> of the job with the given
    /// <paramref name="id"/> (leaving lock and schedule fields untouched, so it never races the keepalive).
    /// Called by the scheduler while a task reports progress. The default implementation does nothing —
    /// override it (a targeted single-column update) to persist progress in a custom store.
    /// </summary>
    Task UpdateProgressAsync(string id, decimal progress, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Inserts a <see cref="JobExecutionStatus.Running"/> execution-history record when a run starts. The
    /// scheduler calls this (before invoking the task) only when execution history is enabled via
    /// <c>WithExecutionHistory</c>. The record is finalized by <see cref="RecordExecutionFinishedAsync"/>,
    /// matched on <see cref="CronnerJobExecution.Id"/>. The default implementation does nothing — override it
    /// (an insert) to persist history in a custom store.
    /// </summary>
    Task RecordExecutionStartedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Finalizes the execution-history record with id <see cref="CronnerJobExecution.Id"/> — the row inserted
    /// by <see cref="RecordExecutionStartedAsync"/> — setting its terminal status, finish time and error.
    /// The default implementation does nothing — override it (an update matched on the id) to persist history.
    /// </summary>
    Task RecordExecutionFinishedAsync(CronnerJobExecution execution, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Returns up to <paramref name="limit"/> execution-history records for the job with the given
    /// <paramref name="jobId"/>, ordered newest first (by <see cref="CronnerJobExecution.StartedAt"/>). The
    /// default implementation returns an empty list — override it to expose history from a custom store.
    /// </summary>
    Task<IReadOnlyList<CronnerJobExecution>> GetExecutionsAsync(string jobId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CronnerJobExecution>>(Array.Empty<CronnerJobExecution>());

    /// <summary>
    /// Deletes execution-history records for the given <paramref name="jobId"/>, keeping the newest
    /// <paramref name="keepNewest"/> (by <see cref="CronnerJobExecution.StartedAt"/>) and removing the rest.
    /// The scheduler calls this after each run finishes when history retention is enabled. The default
    /// implementation does nothing — override it (a targeted delete) to enable retention in a custom store.
    /// </summary>
    Task PruneExecutionsAsync(string jobId, int keepNewest, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Finalizes every execution-history record of the job with the given <paramref name="jobId"/> that is still
    /// <see cref="JobExecutionStatus.Running"/>: sets its status to <see cref="JobExecutionStatus.Failed"/>,
    /// <see cref="CronnerJobExecution.FinishedAt"/> to <paramref name="finishedAt"/> and
    /// <see cref="CronnerJobExecution.Error"/> to <paramref name="error"/>. The scheduler calls this right before it
    /// records a new run of a non-concurrent task: once it holds the task's lock, any older <c>Running</c> row
    /// belongs to a run whose owner crashed or lost its lease without finalizing — an orphan. The error text is
    /// <see cref="CronnerExecutionErrors.Orphaned"/>. The default implementation does nothing — override it (a
    /// targeted update) so history stays self-consistent in a custom store.
    /// </summary>
    /// <returns>The number of records finalized.</returns>
    Task<int> FinalizeOrphanedExecutionsAsync(string jobId, DateTimeOffset finishedAt, string error, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
