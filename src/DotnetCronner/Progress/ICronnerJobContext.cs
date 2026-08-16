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
    /// <summary>The most recent total progress reported for this execution.</summary>
    decimal TotalProgress { get; }

    /// <summary>Reports total progress (fire-and-forget; hooks run in the background and are drained before the terminal hook).</summary>
    void Progress(decimal value);

    /// <summary>Reports total progress and awaits the <c>OnTotalProgressChange</c> hooks.</summary>
    Task ProgressAsync(decimal value);

    /// <summary>Opens a progress scope for a subtask/category. Dispose it (or <c>await using</c>) to close it.</summary>
    ICronnerProgressScope OpenProgressScope(string? category = null);
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

    /// <summary>Reports scope progress (fire-and-forget).</summary>
    void Progress(decimal value);

    /// <summary>Reports scope progress and awaits the <c>OnScopeProgress</c> hooks.</summary>
    Task ProgressAsync(decimal value);
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
