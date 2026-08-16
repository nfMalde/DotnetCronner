namespace DotnetCronner;

/// <summary>
/// A lightweight wake-up signal so the scheduler can react immediately to manually triggered tasks
/// instead of waiting for the next poll tick.
/// </summary>
public sealed class CronnerScheduleSignal
{
    private readonly SemaphoreSlim _semaphore = new(0);

    /// <summary>Wakes the scheduler.</summary>
    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the pending poll will pick up the work.
        }
    }

    /// <summary>Waits until signalled or <paramref name="timeout"/> elapses.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}
