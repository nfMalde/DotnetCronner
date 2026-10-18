namespace DotnetCronner;

/// <summary>
/// What the DotnetCronner health check looks at. Every probe can be turned off, and the two that are about
/// "slow" rather than "broken" report <c>Degraded</c> so a struggling scheduler does not take an otherwise
/// healthy application out of rotation.
/// </summary>
/// <remarks>
/// Deliberately absent: job failures. A task that throws is the application's problem to handle — through
/// <c>OnFail</c>, the <c>cronner.jobs.failed</c> metric, or execution history — not a reason to report the
/// whole process unhealthy and have an orchestrator restart it.
/// </remarks>
public sealed class CronnerHealthCheckOptions
{
    /// <summary>
    /// Whether to confirm the store is reachable by calling <c>ICronnerStore.PingAsync</c>. Defaults to
    /// <c>true</c>. A scheduler that cannot reach its store cannot run anything, so this reports
    /// <c>Unhealthy</c>.
    /// </summary>
    public bool CheckStore { get; set; } = true;

    /// <summary>
    /// How long the store ping may take before the check gives up on it. Defaults to 5 seconds — a health
    /// probe must answer promptly even when the database does not.
    /// </summary>
    public TimeSpan StoreTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to confirm the scheduler is still polling for due work. Defaults to <c>true</c>. A poll loop
    /// that has stopped turning means nothing will ever run again, so this reports <c>Unhealthy</c>.
    /// </summary>
    public bool CheckPolling { get; set; } = true;

    /// <summary>
    /// How many polling intervals may pass without a completed poll before the scheduler counts as wedged.
    /// Defaults to 5. Used only when <see cref="MaxTimeSincePoll"/> is not set.
    /// </summary>
    public int MissedPollsAllowed { get; set; } = 5;

    /// <summary>
    /// An absolute staleness limit for the last completed poll, overriding
    /// <see cref="MissedPollsAllowed"/> × <c>CronnerOptions.PollingInterval</c>. <c>null</c> — the default —
    /// derives it, so a long polling interval does not produce false alarms.
    /// </summary>
    public TimeSpan? MaxTimeSincePoll { get; set; }

    /// <summary>
    /// How long a single execution may run before the check reports <c>Degraded</c>. <c>null</c> — the
    /// default — disables the probe, because only you know what "too long" means for your jobs. Set it
    /// comfortably above your slowest job: this is for detecting a wedged run, not a busy one.
    /// </summary>
    public TimeSpan? StalledRunThreshold { get; set; }
}
