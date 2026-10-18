using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DotnetCronner;

/// <summary>
/// Reports whether the DotnetCronner scheduler in this process is working: it started, its poll loop is
/// still turning, and it can reach its store. Register it with
/// <c>services.AddHealthChecks().AddCronner()</c>.
/// </summary>
/// <remarks>
/// The distinction this check draws is between <em>the scheduler is broken</em> (<c>Unhealthy</c> — nothing
/// will run until someone intervenes) and <em>the scheduler is struggling</em> (<c>Degraded</c> — work is
/// still moving, just not well). Failing jobs are neither: see <see cref="CronnerHealthCheckOptions"/>.
/// </remarks>
public sealed class CronnerHealthCheck : IHealthCheck
{
    private readonly CronnerSchedulerDiagnostics _diagnostics;
    private readonly CronnerOptions _options;
    private readonly CronnerHealthCheckOptions _checkOptions;
    private readonly IServiceProvider _services;

    /// <summary>Creates the health check.</summary>
    public CronnerHealthCheck(
        CronnerSchedulerDiagnostics diagnostics,
        CronnerOptions options,
        CronnerHealthCheckOptions checkOptions,
        IServiceProvider services)
    {
        _diagnostics = diagnostics;
        _options = options;
        _checkOptions = checkOptions;
        _services = services;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["started"] = _diagnostics.IsStarted,
            ["running"] = _diagnostics.RunningCount,
            ["polls"] = _diagnostics.PollCount,
        };

        if (_diagnostics.LastPollUtc is { } lastPoll)
            data["lastPollUtc"] = lastPoll;

        if (!_diagnostics.IsStarted)
            return Unhealthy("The DotnetCronner scheduler has not started.", data);

        // 1. Is the loop still turning? A scheduler that stopped polling will never run anything again, and
        //    nothing else here would notice — the store can be perfectly healthy while this is true.
        if (_checkOptions.CheckPolling)
        {
            var limit = _checkOptions.MaxTimeSincePoll
                ?? _options.PollingInterval * Math.Max(1, _checkOptions.MissedPollsAllowed);

            // Allow one full limit of grace before the first poll lands, so a just-started host is not
            // reported unhealthy for the gap between "started" and "polled once".
            var since = DateTimeOffset.UtcNow - (_diagnostics.LastPollUtc ?? _diagnostics.StartedUtc!.Value);
            data["timeSinceLastPoll"] = since;
            if (since > limit)
            {
                return Unhealthy(
                    $"The DotnetCronner scheduler has not completed a poll for {since:g} (limit {limit:g}); its poll loop appears to be stuck.",
                    data);
            }
        }

        // 2. Can we still reach the store? Asked through a scope so a store registered with
        //    CronnerStoreLifetime.Scoped resolves exactly as it does for the scheduler itself.
        if (_checkOptions.CheckStore)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_checkOptions.StoreTimeout);

            try
            {
                await using var scope = _services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ICronnerStore>();
                await store.PingAsync(timeout.Token).ConfigureAwait(false);
                data["store"] = "reachable";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                data["store"] = "timeout";
                return Unhealthy(
                    $"The DotnetCronner store did not answer within {_checkOptions.StoreTimeout:g}.", data);
            }
            catch (Exception ex)
            {
                data["store"] = "unreachable";
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    "The DotnetCronner store is unreachable.",
                    ex,
                    data);
            }
        }

        // 3. Anything wedged? Degraded, not unhealthy: work is still moving, one run is just taking too long,
        //    and restarting the process over it would kill the other runs too.
        if (_checkOptions.StalledRunThreshold is { } threshold &&
            _diagnostics.LongestRunningDuration is { } longest)
        {
            data["longestRunningDuration"] = longest;
            if (longest > threshold)
            {
                return HealthCheckResult.Degraded(
                    $"A DotnetCronner execution has been running for {longest:g}, past the {threshold:g} stalled-run threshold.",
                    data: data);
            }
        }

        return HealthCheckResult.Healthy("The DotnetCronner scheduler is running.", data);

        HealthCheckResult Unhealthy(string message, IReadOnlyDictionary<string, object> payload) =>
            new(context.Registration.FailureStatus, message, data: payload);
    }
}
