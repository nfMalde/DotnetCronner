using System.Reflection;

namespace DotnetCronner;

/// <summary>
/// Runtime options for the DotnetCronner scheduler.
/// </summary>
public sealed class CronnerOptions
{
    /// <summary>The time zone cron expressions are evaluated in. Defaults to UTC.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>How often the scheduler polls the store for due tasks. Defaults to 1 second.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of tasks executed concurrently across the whole scheduler. Defaults to the processor count.</summary>
    public int MaxConcurrentTasks { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// How long an execution claim (lock) stays valid before another worker may reclaim a stalled task.
    /// While a task runs, the scheduler renews its lock roughly every <c>LockTtl</c>/2, so a healthy long
    /// execution keeps its claim indefinitely; a task is treated as stalled (and becomes reclaimable) only
    /// after a worker stops renewing for longer than this window — for example after a crash or a long GC
    /// pause. Defaults to 1 minute.
    /// </summary>
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often a running task's execution lock is renewed (and the <c>OnKeepAlive</c> hook fired). When
    /// <c>null</c> (the default), the cadence is <see cref="LockTtl"/>/2. Set this to pin the keepalive
    /// frequency independently — keep it comfortably below <see cref="LockTtl"/> so the lock never lapses
    /// between renewals.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }

    /// <summary>
    /// How many finished one-off (enqueued) rows to keep per definition; older ones are pruned after each
    /// one-off completes. <c>0</c> (the default) keeps them all — retention is off.
    /// </summary>
    public int OneOffRetentionCount { get; set; }

    /// <summary>Number of automatic retries after a failed execution. Defaults to 0 (no retry).</summary>
    public int DefaultMaxRetries { get; set; }

    /// <summary>Delay between retries. Defaults to <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Whether to scan the entry assembly for <see cref="CronnerTaskAttribute"/> tasks. Defaults to <c>true</c>.</summary>
    public bool ScanEntryAssembly { get; set; } = true;

    /// <summary>Additional assemblies to scan for attribute-based tasks.</summary>
    internal List<Assembly> AdditionalAssemblies { get; } = [];

    /// <summary>
    /// When non-empty, attribute discovery only considers types assignable to one of these interfaces or
    /// base classes. Empty means every type in the scanned assemblies is considered.
    /// </summary>
    internal List<Type> DiscoveryTypeFilters { get; } = [];
}
