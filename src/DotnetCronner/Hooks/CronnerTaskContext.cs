using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// The context passed to a lifecycle or lock hook. Each hook method is invoked in its own fresh DI scope;
/// <see cref="Services"/> is that scope's provider. <see cref="Duration"/> and <see cref="Exception"/> are
/// populated for the terminal (succeeded / failed / cancelled) events and are otherwise their defaults.
/// </summary>
public sealed class CronnerTaskContext
{
    /// <summary>A snapshot of the task the hook is firing for.</summary>
    public required CronnerJob Job { get; init; }

    /// <summary>The hook's own scoped service provider — resolve scoped services here.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>A cancellation token tied to the scheduler's lifetime; cancelled when the host is shutting down.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>How long the execution took. Zero outside the terminal events.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>The exception thrown by a failed execution, if any.</summary>
    public Exception? Exception { get; internal set; }

    /// <summary>
    /// On the <c>OnFail</c> event, whether the scheduler will retry this run (there are attempts left). When
    /// <c>true</c>, this failure is not final — e.g. hold off on the alert/mail until it is <c>false</c>.
    /// Always <c>false</c> outside <c>OnFail</c>.
    /// </summary>
    public bool WillRetry { get; internal set; }

    /// <summary>This run's shared state bag, set by the scheduler. Backs <see cref="Set{T}"/>/<see cref="Get{T}"/>.</summary>
    internal CronnerRunState? RunState { get; init; }

    /// <summary>
    /// The id of the execution this hook fires for — the <see cref="CronnerJobExecution.Id"/> of the run in
    /// flight, so your own per-run record (a log file, a display label) can be keyed to the scheduler's
    /// execution history. The same value is visible to the job body via <see cref="ICronnerJobContext.ExecutionId"/>
    /// and to every hook of the run, from <c>OnLockAcquire</c> through the terminal event. It is generated for
    /// every run, whether or not execution history is persisted. <b>A retry is a new execution:</b> each
    /// attempt gets its own id (and <see cref="CronnerJobExecution.Attempt"/> increments).
    /// </summary>
    public string ExecutionId => RunState?.ExecutionId ?? string.Empty;

    /// <summary>
    /// The effective execution-lock TTL in force (<see cref="CronnerOptions.LockTtl"/>): a task whose lock is
    /// not renewed for this long becomes reclaimable by another instance. Read-only — configure it on the
    /// builder; read it here to derive a staleness threshold for your own run record instead of hardcoding one.
    /// </summary>
    public TimeSpan LockTtl { get; init; }

    /// <summary>
    /// The effective keepalive cadence in force — how often a running task's lock is renewed and
    /// <c>OnKeepAlive</c> fires (<see cref="CronnerOptions.KeepAliveInterval"/>, or <see cref="LockTtl"/>/2 when
    /// unset). Read-only; a run with no heartbeat for a few multiples of this is stalled, and one with none for
    /// more than <see cref="LockTtl"/> can be reclaimed.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; }

    /// <summary>The total progress reported, for the <c>OnTotalProgressChange</c> event (and current total on scope events).</summary>
    public decimal TotalProgress { get; internal set; }

    /// <summary>A snapshot of the progress scope, for the scope events (opened / progress / closed); otherwise <c>null</c>.</summary>
    public CronnerProgressInfo? ProgressScope { get; internal set; }

    /// <summary>
    /// The custom payload passed to the <c>Progress(...)</c> / <c>OpenProgressScope(...)</c> call that raised
    /// a progress event (total or scope) — whatever object the job supplied, or <c>null</c>. Only set for the
    /// progress events.
    /// </summary>
    public object? ProgressPayload { get; internal set; }

    /// <summary>
    /// Reads a value the job (or an earlier hook) placed in this run's state bag via
    /// <see cref="ICronnerJobContext.Set{T}"/>, or its default if none is set. This is the scope-independent
    /// way to pass data from the job to a hook — it works even when hooks run in their own DI scope.
    /// </summary>
    public T? Get<T>() => RunState is { } state ? state.Get<T>() : default;

    /// <summary>Reads a value from this run's state bag; returns <c>false</c> if none of type <typeparamref name="T"/> is set.</summary>
    public bool TryGet<T>(out T value)
    {
        if (RunState is { } state)
            return state.TryGet(out value);

        value = default!;
        return false;
    }

    /// <summary>Stores <paramref name="value"/> in this run's state bag so a later hook of the same run can read it.</summary>
    public void Set<T>(T value) where T : notnull => RunState?.Set(value);

    /// <summary>Sets the object persisted onto this run's execution-history record (its <c>Data</c> slot).</summary>
    /// <remarks>Deprecated — see <see cref="ICronnerJobContext.SetExecutionData"/>.</remarks>
    [Obsolete("Execution history records an execution, not application data — keep app data/logs in your own store, correlated by ctx.ExecutionId. Removed in a future release.")]
    public void SetExecutionData(object? data)
    {
        if (RunState is { } state)
            state.ExecutionData = data;
    }

    /// <summary>
    /// Reads back the object the job or an earlier hook of this run placed in the execution-data slot via
    /// <see cref="SetExecutionData"/> / <see cref="ICronnerJobContext.SetExecutionData"/>, so a later hook can
    /// augment it rather than keep its own copy. Returns <c>false</c> if nothing of type <typeparamref name="T"/>
    /// is set. (To read what a <em>previous run</em> recorded, query <c>ICronnerClient.GetExecutionsAsync</c>.)
    /// </summary>
    /// <remarks>Deprecated — see <see cref="ICronnerJobContext.SetExecutionData"/>.</remarks>
    [Obsolete("Execution history records an execution, not application data — keep app data/logs in your own store, correlated by ctx.ExecutionId. Removed in a future release.")]
    public bool TryGetExecutionData<T>(out T value)
    {
        if (RunState?.ExecutionData is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Resolves a required service from this hook's scope — the same idea as <c>HasParam</c> in a <c>Sched</c> lambda.</summary>
    public T HasParam<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Resolves a value from this hook's scope using a caller-supplied factory.</summary>
    public T HasParam<T>(Func<IServiceProvider, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory(Services);
    }
}
