using CronTestApp.Services;

namespace CronTestApp.Jobs;

/// <summary>
/// Targets for the fluent <c>Sched&lt;LambdaJobs&gt;(x =&gt; ...)</c> registrations in
/// <see cref="Configuration.CronnerSetup"/>. Nothing here carries an attribute — these methods are only
/// tasks because Program.cs schedules them, which is exactly what makes them a test of the lambda
/// surface (literals, <c>HasParam&lt;T&gt;()</c>, the factory overload and async methods).
/// </summary>
public sealed class LambdaJobs(JobActivityLog activity, ILogger<LambdaJobs> logger)
{
    /// <summary>Literal arguments are captured from the expression; the token comes from the task.</summary>
    public void SayHello(string who, int times, CancellationToken cancellationToken)
    {
        for (var i = 0; i < times; i++)
            logger.LogInformation("Hello {Who} ({Index}/{Times})", who, i + 1, times);

        activity.Record("lambda:hello", $"literal arguments captured from the expression: who='{who}', times={times}");
    }

    /// <summary><c>HasParam&lt;IGreeter&gt;()</c> — resolved from the execution scope at run time.</summary>
    public void UseService(IGreeter greeter, ScopeMarker scope, CancellationToken cancellationToken)
    {
        greeter.Greet("lambda task", "lambda:service");
        activity.Record("lambda:service", $"HasParam<IGreeter>() + HasParam<ScopeMarker>() resolved from DI (scope {scope.Id})");
    }

    /// <summary>
    /// The factory overload — <c>HasParam&lt;Tenant&gt;(sp =&gt; sp.GetRequiredService&lt;ITenantAccessor&gt;().Current)</c>
    /// — for values that are not registered services themselves.
    /// </summary>
    public void ForTenant(Tenant tenant) =>
        activity.Record("lambda:tenant", $"HasParam<Tenant>(sp => ...) factory produced tenant '{tenant.Name}'");

    /// <summary>An async target: the returned <see cref="Task"/> is awaited before the run counts as finished.</summary>
    public async Task ImportAsync(int batchSize, CancellationToken cancellationToken)
    {
        activity.Record("lambda:import", $"async task started (batchSize={batchSize})");
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        activity.Record("lambda:import", "async task finished — the scheduler awaited the returned Task");
    }

    /// <summary>Scheduled without a cron string, so it only runs on <c>POST /tasks/lambda:manual/run</c>.</summary>
    public void ManualOnly() =>
        activity.Record("lambda:manual", "lambda task registered without a cron string (manual only)");
}
