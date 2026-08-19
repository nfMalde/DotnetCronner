namespace DotnetCronner;

/// <summary>
/// The per-execution context a running task pulls in from DI to report progress. Resolve it like any
/// other service — constructor injection or <c>HasParam&lt;ICronnerJobContext&gt;()</c> in a <c>Sched</c>
/// lambda. Report overall progress directly with <see cref="Progress"/> / <see cref="ProgressAsync"/>, and
/// open <see cref="OpenProgressScope"/> for a subtask/category whose progress is reported independently.
/// </summary>
/// <remarks>
/// Progress values are a <c>0..1</c> fraction by convention (<c>0.5m</c> = 50%); they are passed through
/// to hooks verbatim and never clamped, so you may use another scale if you prefer. Outside a task run the
/// context is inert (reporting is a no-op).
/// </remarks>
public interface ICronnerJobContext
{
    /// <summary>
    /// The id of the execution in flight — the <see cref="CronnerJobExecution.Id"/> of this run — so your own
    /// per-run record (a log file, a display label, a foreign key) can be keyed to the scheduler's execution
    /// history. The same value reaches every hook of the run as <see cref="CronnerTaskContext.ExecutionId"/>. It
    /// is generated for every run even when history is not persisted; <b>a retry is a new execution</b> with a
    /// new id. Empty outside a task run.
    /// </summary>
    string ExecutionId { get; }

    /// <summary>The most recent total progress reported for this execution.</summary>
    decimal TotalProgress { get; }

    /// <summary>
    /// Reports total progress (fire-and-forget; hooks run in the background and are drained before the
    /// terminal hook). Pass an optional <paramref name="payload"/> — any object — delivered to the
    /// <c>OnTotalProgressChange</c> hook as <see cref="CronnerTaskContext.ProgressPayload"/> for this report
    /// (e.g. the current step name).
    /// </summary>
    void Progress(decimal value, object? payload = null);

    /// <summary>Reports total progress and awaits the <c>OnTotalProgressChange</c> hooks. See <see cref="Progress"/> for <paramref name="payload"/>.</summary>
    Task ProgressAsync(decimal value, object? payload = null);

    /// <summary>
    /// Opens a progress scope for a subtask/category. Dispose it (or <c>await using</c>) to close it. The
    /// optional <paramref name="payload"/> is delivered to the <c>OnProgressScopeOpened</c> hook.
    /// </summary>
    ICronnerProgressScope OpenProgressScope(string? category = null, object? payload = null);

    /// <summary>
    /// Stores <paramref name="value"/> in this run's state bag under its type, for a hook of the same run to
    /// read via <see cref="CronnerTaskContext.Get{T}"/> — including on the failure path (<c>OnFail</c>). The
    /// bag is per run, so concurrent runs never share it. (<c>OnStart</c> fires before the job body, so it
    /// cannot see values set here.)
    /// </summary>
    void Set<T>(T value) where T : notnull;

    /// <summary>Reads a value previously placed in this run's state bag, or its default if none is set.</summary>
    T? Get<T>();

    /// <summary>Reads a value from this run's state bag; returns <c>false</c> if none of type <typeparamref name="T"/> is set.</summary>
    bool TryGet<T>(out T value);

    /// <summary>
    /// Sets the object persisted (as JSON) onto this run's execution-history record (its <c>Data</c> slot),
    /// so a store row can carry your own summary/log reference alongside the built-in fields. Requires
    /// execution history to be enabled; <c>null</c> writes no data.
    /// </summary>
    void SetExecutionData(object? data);

    /// <summary>
    /// Reads back the object currently in this run's execution-data slot (set by the job or an earlier hook of
    /// the same run via <see cref="SetExecutionData"/>), so it can be augmented rather than duplicated. Returns
    /// <c>false</c> if nothing of type <typeparamref name="T"/> is set.
    /// </summary>
    bool TryGetExecutionData<T>(out T value);
}

/// <summary>A subtask/category progress scope opened from <see cref="ICronnerJobContext.OpenProgressScope"/>.</summary>
public interface ICronnerProgressScope : IDisposable, IAsyncDisposable
{
    /// <summary>A stable id for this scope within the execution.</summary>
    string Id { get; }

    /// <summary>The category label passed when the scope was opened, if any.</summary>
    string? Category { get; }

    /// <summary>The most recent progress reported to this scope.</summary>
    decimal Value { get; }

    /// <summary>Reports scope progress (fire-and-forget). The optional <paramref name="payload"/> reaches the <c>OnScopeProgress</c> hook.</summary>
    void Progress(decimal value, object? payload = null);

    /// <summary>Reports scope progress and awaits the <c>OnScopeProgress</c> hooks. See <see cref="Progress"/> for <paramref name="payload"/>.</summary>
    Task ProgressAsync(decimal value, object? payload = null);
}

/// <summary>An immutable snapshot of a progress scope, handed to progress hooks via <see cref="CronnerTaskContext.ProgressScope"/>.</summary>
public sealed class CronnerProgressInfo
{
    /// <summary>The scope id.</summary>
    public required string Id { get; init; }

    /// <summary>The scope category, if any.</summary>
    public string? Category { get; init; }

    /// <summary>The scope progress at the moment the hook fired.</summary>
    public decimal Value { get; init; }
}
