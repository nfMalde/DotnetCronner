using System.Collections.Concurrent;

namespace DotnetCronner;

/// <summary>
/// A per-run, in-memory, typed state bag shared by the running job and all of its hooks. Created once per
/// execution and threaded through every hook invocation, so a value the job stashes is visible to
/// <c>OnKeepAlive</c>, <c>OnSuccess</c>, <c>OnFail</c> and <c>OnCancel</c> for the same run — even when the
/// hooks run in their own DI scope. It is keyed per run (not per task), so concurrent runs of the same task
/// never share a bag. Thread-safe.
/// </summary>
/// <remarks>
/// Consumers do not use this type directly; they call <c>Set</c> / <c>Get</c> / <c>TryGet</c> on
/// <see cref="ICronnerJobContext"/> (from the job) or <see cref="CronnerTaskContext"/> (from a hook), which
/// delegate here. Assign <see cref="ExecutionData"/> (or call <c>SetExecutionData</c>) to have an object
/// persisted onto the run's execution-history record.
/// </remarks>
internal sealed class CronnerRunState
{
    private readonly ConcurrentDictionary<Type, object?> _items = new();

    /// <summary>Creates the bag for one run, identified by <paramref name="executionId"/>.</summary>
    public CronnerRunState(string executionId) => ExecutionId = executionId;

    /// <summary>
    /// The id of the execution this bag belongs to — the <see cref="CronnerJobExecution.Id"/> of the run in
    /// flight. Generated per run (a retry is a new run with a new id), whether or not history is persisted.
    /// </summary>
    public string ExecutionId { get; }

    /// <summary>The history record for this run, when execution history is enabled; otherwise <c>null</c>.</summary>
    public CronnerJobExecution? Execution { get; set; }

    /// <summary>Stores <paramref name="value"/> under its runtime type, replacing any existing value of that type.</summary>
    public void Set<T>(T value) where T : notnull => _items[typeof(T)] = value;

    /// <summary>Returns the stored value of type <typeparamref name="T"/>, or its default if none is set.</summary>
    public T? Get<T>() => _items.TryGetValue(typeof(T), out var value) && value is T typed ? typed : default;

    /// <summary>Gets the stored value of type <typeparamref name="T"/>; returns <c>false</c> if none is set.</summary>
    public bool TryGet<T>(out T value)
    {
        if (_items.TryGetValue(typeof(T), out var stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Whether a value of type <typeparamref name="T"/> is set.</summary>
    public bool Has<T>() => _items.ContainsKey(typeof(T));

    /// <summary>Removes the stored value of type <typeparamref name="T"/>, if any.</summary>
    public bool Remove<T>() => _items.TryRemove(typeof(T), out _);

    /// <summary>
    /// The object serialized (as JSON) onto this run's execution-history record's <c>Data</c> slot when the
    /// run finalizes. <c>null</c> writes no data. Only used when execution history is enabled.
    /// </summary>
    public object? ExecutionData { get; set; }
}
