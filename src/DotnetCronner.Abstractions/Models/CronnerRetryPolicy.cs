namespace DotnetCronner;

/// <summary>
/// How a failing task is retried: how many attempts it gets in total and how long to wait between them.
/// Attach one per task (<c>WithRetryPolicy(...)</c> or the <c>[CronnerTask]</c> retry properties) or set a
/// default for every task via <c>CronnerOptions.DefaultRetryPolicy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxAttempts"/> counts <em>total attempts including the first run</em>, so <c>MaxAttempts = 3</c>
/// is one run plus two retries, and <c>MaxAttempts = 1</c> (the default) means no retry at all.
/// </para>
/// <para>
/// Each attempt is its own execution with its own execution id and an incremented
/// <see cref="CronnerJobExecution.Attempt"/>, so a retry always stays distinguishable from a fresh cron
/// occurrence. While attempts remain, the task is rescheduled at the retry delay rather than at its next cron
/// time; the cron schedule resumes once an attempt succeeds or the attempts run out.
/// </para>
/// </remarks>
public sealed class CronnerRetryPolicy
{
    /// <summary>The maximum delay a policy applies when none is configured explicitly: one hour.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromHours(1);

    /// <summary>A policy that never retries — a single attempt. This is the behavior when nothing is configured.</summary>
    public static CronnerRetryPolicy None { get; } = new(1, CronnerRetryStrategy.Immediate, TimeSpan.Zero);

    /// <summary>Creates a policy.</summary>
    /// <param name="maxAttempts">Total attempts including the first run. Must be at least 1.</param>
    /// <param name="strategy">How the delay before each retry is calculated.</param>
    /// <param name="delay">The base delay: the wait itself for <see cref="CronnerRetryStrategy.Fixed"/>, the first wait for <see cref="CronnerRetryStrategy.Exponential"/>.</param>
    /// <param name="maxDelay">The ceiling on exponential growth. Defaults to <see cref="DefaultMaxDelay"/>.</param>
    /// <param name="jitter">Whether to spread the computed delay randomly — see <see cref="Jitter"/>.</param>
    public CronnerRetryPolicy(
        int maxAttempts,
        CronnerRetryStrategy strategy = CronnerRetryStrategy.Default,
        TimeSpan delay = default,
        TimeSpan? maxDelay = null,
        bool jitter = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "A retry delay cannot be negative.");

        var cap = maxDelay ?? DefaultMaxDelay;
        if (cap <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), cap, "A maximum retry delay must be greater than zero.");

        MaxAttempts = maxAttempts;
        Strategy = strategy;
        Delay = delay;
        MaxDelay = cap;
        Jitter = jitter;
    }

    /// <summary>Total attempts including the first run. <c>1</c> means the task is never retried.</summary>
    public int MaxAttempts { get; }

    /// <summary>How the delay before each retry is calculated.</summary>
    public CronnerRetryStrategy Strategy { get; }

    /// <summary>
    /// The base delay — the wait itself under <see cref="CronnerRetryStrategy.Fixed"/>, the first wait (then
    /// doubled) under <see cref="CronnerRetryStrategy.Exponential"/>, and unused under
    /// <see cref="CronnerRetryStrategy.Immediate"/>.
    /// </summary>
    public TimeSpan Delay { get; }

    /// <summary>
    /// The ceiling on the growth of <see cref="CronnerRetryStrategy.Exponential"/> backoff, so a long run of
    /// failures cannot push the next attempt arbitrarily far out. Defaults to <see cref="DefaultMaxDelay"/>.
    /// It does not clamp <see cref="CronnerRetryStrategy.Fixed"/>: a delay written out literally is used as
    /// written.
    /// </summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Whether the computed delay is spread randomly so that many tasks failing together (a database outage,
    /// say) do not all retry at the same instant. The actual wait is drawn uniformly from
    /// <c>[delay/2, delay]</c> — never longer than the configured delay and never collapsing to zero.
    /// </summary>
    public bool Jitter { get; }

    /// <summary>
    /// Whether another attempt follows <paramref name="failedAttempt"/> (1-based: <c>1</c> is the first run).
    /// </summary>
    public bool AllowsRetry(int failedAttempt) => failedAttempt < MaxAttempts;

    /// <summary>
    /// The delay to wait after <paramref name="failedAttempt"/> (1-based) before the next attempt. With
    /// <see cref="Jitter"/> enabled this is random, so call it once per retry decision and reuse the value —
    /// the scheduler does, which is why the wait a hook sees on <c>ctx.RetryDelay</c> is the wait actually used.
    /// </summary>
    public TimeSpan GetDelay(int failedAttempt)
    {
        var attempt = Math.Max(1, failedAttempt);
        var strategy = Strategy == CronnerRetryStrategy.Default
            ? (Delay > TimeSpan.Zero ? CronnerRetryStrategy.Fixed : CronnerRetryStrategy.Immediate)
            : Strategy;

        var delay = strategy switch
        {
            CronnerRetryStrategy.Immediate => TimeSpan.Zero,
            CronnerRetryStrategy.Exponential => Double(Delay, attempt - 1, MaxDelay),
            _ => Delay,
        };

        if (!Jitter || delay <= TimeSpan.Zero)
            return delay;

        // Equal jitter: keep half the computed delay and randomize the other half. Full jitter (0..delay)
        // spreads a little better but can fire a "5 second" retry almost immediately, which surprises people
        // reading their own configuration back.
        var half = delay.Ticks / 2;
        return TimeSpan.FromTicks(half + Random.Shared.NextInt64(delay.Ticks - half + 1));
    }

    /// <summary>A policy that retries immediately, up to <paramref name="maxAttempts"/> attempts in total.</summary>
    public static CronnerRetryPolicy Immediate(int maxAttempts) =>
        new(maxAttempts, CronnerRetryStrategy.Immediate, TimeSpan.Zero);

    /// <summary>A policy that waits <paramref name="delay"/> before every retry.</summary>
    public static CronnerRetryPolicy FixedDelay(int maxAttempts, TimeSpan delay, bool jitter = false) =>
        new(maxAttempts, CronnerRetryStrategy.Fixed, delay, jitter: jitter);

    /// <summary>A policy that doubles the wait after each failed attempt, starting at <paramref name="delay"/>.</summary>
    public static CronnerRetryPolicy ExponentialBackoff(int maxAttempts, TimeSpan delay, TimeSpan? maxDelay = null, bool jitter = false) =>
        new(maxAttempts, CronnerRetryStrategy.Exponential, delay, maxDelay, jitter);

    /// <summary>
    /// Builds the policy described by the retry properties on a <see cref="CronnerTaskAttribute"/>, or
    /// <c>null</c> when the attribute configures no retries at all (the task then inherits the scheduler's
    /// default policy). Validates at registration time so a nonsensical policy fails fast, naming the task.
    /// </summary>
    /// <param name="taskId">The task id, used only to name the task in the error message.</param>
    /// <param name="attribute">The attribute to read.</param>
    /// <exception cref="InvalidOperationException">The retry properties do not describe a usable policy.</exception>
    public static CronnerRetryPolicy? FromTaskAttribute(string taskId, CronnerTaskAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var configured = attribute.MaxAttempts != 0
            || attribute.RetryStrategy != CronnerRetryStrategy.Default
            || attribute.RetryDelaySeconds != 0
            || attribute.RetryMaxDelaySeconds != 0
            || attribute.RetryJitter;

        if (!configured)
            return null;

        // The other properties only describe *how* to retry. Without an attempt count there is nothing to
        // describe, and silently ignoring them would leave a task that looks retried but never is.
        if (attribute.MaxAttempts < 1)
            throw new InvalidOperationException(
                $"DotnetCronner task '{taskId}' sets retry properties on [CronnerTask] but no MaxAttempts. " +
                "Set MaxAttempts (total attempts including the first run, so 3 means one run plus two retries), " +
                "or remove the retry properties to inherit the scheduler's default retry policy.");

        if (attribute.RetryDelaySeconds < 0)
            throw new InvalidOperationException(
                $"DotnetCronner task '{taskId}' has a negative RetryDelaySeconds ({attribute.RetryDelaySeconds}).");

        if (attribute.RetryMaxDelaySeconds < 0)
            throw new InvalidOperationException(
                $"DotnetCronner task '{taskId}' has a negative RetryMaxDelaySeconds ({attribute.RetryMaxDelaySeconds}).");

        return new CronnerRetryPolicy(
            attribute.MaxAttempts,
            attribute.RetryStrategy,
            TimeSpan.FromSeconds(attribute.RetryDelaySeconds),
            attribute.RetryMaxDelaySeconds > 0 ? TimeSpan.FromSeconds(attribute.RetryMaxDelaySeconds) : null,
            attribute.RetryJitter);
    }

    // Doubling in ticks rather than via Math.Pow: a long-running outage can push the exponent high enough to
    // overflow, and the cap makes everything past it identical anyway, so stop as soon as we are over it.
    private static TimeSpan Double(TimeSpan baseDelay, int doublings, TimeSpan cap)
    {
        if (baseDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var ticks = baseDelay.Ticks;
        for (var i = 0; i < doublings; i++)
        {
            if (ticks >= cap.Ticks)
                return cap;
            ticks *= 2;
        }

        return ticks >= cap.Ticks ? cap : TimeSpan.FromTicks(ticks);
    }
}
