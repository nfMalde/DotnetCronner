using System.Collections.Concurrent;

namespace DotnetCronner;

/// <summary>
/// Tracks currently executing tasks so that <see cref="ICronnerClient.CancelTaskAsync"/> can signal
/// cancellation to a running execution, and so that manual triggers arriving while a task is running
/// can be queued to run immediately afterwards.
/// </summary>
public sealed class CronnerExecutionTracker
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingTriggers = new(StringComparer.Ordinal);

    /// <summary>Registers the cancellation source for a running task (replacing any existing entry — Concurrent-mode runs may overlap).</summary>
    public void Register(string id, CancellationTokenSource cts) => _running[id] = cts;

    /// <summary>
    /// Registers the cancellation source for a running task only if no run of that task is tracked yet. Returns
    /// <c>false</c> — without registering — when one already is, so a non-concurrent task can never be started twice
    /// in this process.
    /// </summary>
    public bool TryRegister(string id, CancellationTokenSource cts) => _running.TryAdd(id, cts);

    /// <summary>Removes the tracking entry for a task once it has finished.</summary>
    public void Unregister(string id) => _running.TryRemove(id, out _);

    /// <summary>Whether a run of the task is currently in progress.</summary>
    public bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>Requests cancellation of a running task. Returns <c>true</c> if the task was running.</summary>
    public bool TryCancel(string id)
    {
        if (!_running.TryGetValue(id, out var cts))
            return false;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The task finished between lookup and cancel; nothing to do.
        }

        return true;
    }

    /// <summary>Records that a manual trigger arrived while the task was running so it runs again once finished.</summary>
    public void MarkTriggerPending(string id) => _pendingTriggers[id] = 0;

    /// <summary>Consumes any pending manual trigger for the task, returning whether one was set.</summary>
    public bool ConsumePendingTrigger(string id) => _pendingTriggers.TryRemove(id, out _);
}
