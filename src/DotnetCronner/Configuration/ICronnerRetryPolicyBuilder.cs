namespace DotnetCronner;

/// <summary>
/// Builds a <see cref="CronnerRetryPolicy"/> fluently, from <c>WithRetryPolicy(...)</c> on the builder (the
/// default for every task) or on a <c>Sched</c> schedule (that task only).
/// </summary>
/// <remarks>
/// Example: <c>policy =&gt; policy.MaxAttempts(5).ExponentialBackoff(TimeSpan.FromSeconds(5)).WithJitter()</c> —
/// one run plus four retries, waiting ~5s, ~10s, ~20s, ~40s, each spread by jitter.
/// </remarks>
public interface ICronnerRetryPolicyBuilder
{
    /// <summary>
    /// Total attempts including the first run — <c>3</c> is one run plus two retries. Defaults to <c>3</c>
    /// when the policy does not set it; <c>1</c> disables retries.
    /// </summary>
    ICronnerRetryPolicyBuilder MaxAttempts(int attempts);

    /// <summary>Retry with no wait between attempts.</summary>
    ICronnerRetryPolicyBuilder Immediate();

    /// <summary>Wait <paramref name="delay"/> before every retry.</summary>
    ICronnerRetryPolicyBuilder FixedDelay(TimeSpan delay);

    /// <summary>
    /// Double the wait after each failed attempt, starting at <paramref name="delay"/> and capped by
    /// <see cref="MaxDelay"/>.
    /// </summary>
    ICronnerRetryPolicyBuilder ExponentialBackoff(TimeSpan delay);

    /// <summary>
    /// Caps how far <see cref="ExponentialBackoff"/> may grow. Defaults to
    /// <see cref="CronnerRetryPolicy.DefaultMaxDelay"/> (one hour). A <see cref="FixedDelay"/> is used as written.
    /// </summary>
    ICronnerRetryPolicyBuilder MaxDelay(TimeSpan maxDelay);

    /// <summary>
    /// Spreads the computed delay randomly over <c>[delay/2, delay]</c>, so tasks that fail together do not
    /// all retry at the same instant. Composes with any strategy.
    /// </summary>
    ICronnerRetryPolicyBuilder WithJitter(bool enabled = true);
}

/// <summary>Default mutable implementation of <see cref="ICronnerRetryPolicyBuilder"/>.</summary>
internal sealed class CronnerRetryPolicyBuilder : ICronnerRetryPolicyBuilder
{
    // Configuring a retry policy at all means retries are wanted, so an unset attempt count is 3 (one run
    // plus two retries) rather than the no-retry 1 that an unconfigured task carries.
    private int _maxAttempts = 3;
    private CronnerRetryStrategy _strategy = CronnerRetryStrategy.Default;
    private TimeSpan _delay = TimeSpan.Zero;
    private TimeSpan? _maxDelay;
    private bool _jitter;

    public ICronnerRetryPolicyBuilder MaxAttempts(int attempts)
    {
        _maxAttempts = attempts;
        return this;
    }

    public ICronnerRetryPolicyBuilder Immediate()
    {
        _strategy = CronnerRetryStrategy.Immediate;
        _delay = TimeSpan.Zero;
        return this;
    }

    public ICronnerRetryPolicyBuilder FixedDelay(TimeSpan delay)
    {
        _strategy = CronnerRetryStrategy.Fixed;
        _delay = delay;
        return this;
    }

    public ICronnerRetryPolicyBuilder ExponentialBackoff(TimeSpan delay)
    {
        _strategy = CronnerRetryStrategy.Exponential;
        _delay = delay;
        return this;
    }

    public ICronnerRetryPolicyBuilder MaxDelay(TimeSpan maxDelay)
    {
        _maxDelay = maxDelay;
        return this;
    }

    public ICronnerRetryPolicyBuilder WithJitter(bool enabled = true)
    {
        _jitter = enabled;
        return this;
    }

    public CronnerRetryPolicy Build() => new(_maxAttempts, _strategy, _delay, _maxDelay, _jitter);

    /// <summary>Runs <paramref name="configure"/> over a fresh builder and returns the policy it describes.</summary>
    public static CronnerRetryPolicy Create(Action<ICronnerRetryPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new CronnerRetryPolicyBuilder();
        configure(builder);
        return builder.Build();
    }
}
