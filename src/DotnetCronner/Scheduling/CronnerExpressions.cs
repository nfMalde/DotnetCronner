namespace DotnetCronner;

/// <summary>
/// Marker helpers used inside <c>Sched</c> lambda expressions to declare how a task method's
/// parameters are supplied at execution time.
/// </summary>
public static class CronnerExpressions
{
    /// <summary>
    /// Declares that a task method parameter is provided at run time rather than as a fixed literal.
    /// A <see cref="System.Threading.CancellationToken"/> is bound to the task's cancellation token;
    /// any other type is resolved from the scoped service provider.
    /// </summary>
    /// <remarks>
    /// This method only carries meaning when it appears inside a <c>Sched</c> expression tree — it is
    /// never actually executed. For example:
    /// <code>
    /// cronner.Sched&lt;ReportJobs&gt;(
    ///     x =&gt; x.SendHourly(x.HasParam&lt;IReportService&gt;(), x.HasParam&lt;CancellationToken&gt;()),
    ///     o =&gt; o.WithCron("0 * * * *"));
    /// </code>
    /// </remarks>
    public static T HasParam<T>(this object instance) => default!;

    /// <summary>
    /// Declares a task method parameter whose value you resolve yourself from the execution scope's
    /// <see cref="IServiceProvider"/> at run time — for example to pull a value off a scoped accessor:
    /// <code>
    /// cronner.Sched&lt;MyJob&gt;(x =&gt; x.Run(
    ///     x.HasParam&lt;Tenant&gt;(sp =&gt; sp.GetRequiredService&lt;ITenantAccessor&gt;().Current)));
    /// </code>
    /// Like the parameterless overload, this only carries meaning inside a <c>Sched</c> expression and is
    /// never actually executed there; the factory is invoked per run with the task's scoped provider.
    /// </summary>
    public static T HasParam<T>(this object instance, Func<IServiceProvider, T> factory) => default!;
}
