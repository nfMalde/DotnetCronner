using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace DotnetCronner;

/// <summary>
/// Emits DotnetCronner's metrics and spans. Registered as a singleton; the scheduler calls it at each
/// lifecycle point. Instruments are created once, and every method here is allocation-light and
/// exception-free — telemetry must never be able to break a run.
/// </summary>
/// <remarks>
/// Both the <see cref="Meter"/> and the <see cref="ActivitySource"/> are base-class-library types, so none
/// of this costs anything until a listener subscribes: an unobserved counter is a no-op, and
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns <c>null</c> with no allocation
/// when nothing is sampling.
/// </remarks>
/// <remarks>
/// Public only because the scheduler's constructor takes it; every member is internal, so there is nothing
/// here for a consumer to call. Subscribe to the telemetry by name instead — see
/// <see cref="CronnerDiagnostics"/>.
/// </remarks>
public sealed class CronnerInstrumentation : IDisposable
{
    private static readonly string Version =
        typeof(CronnerInstrumentation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CronnerInstrumentation).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private readonly Meter _meter;
    private readonly ActivitySource _activitySource;
    private readonly bool _ownsMeter;

    private readonly Counter<long> _started;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _cancelled;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _misfired;
    private readonly Counter<long> _locksAcquired;
    private readonly Counter<long> _locksLost;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _schedulingDelay;

    /// <summary>
    /// Creates the instrumentation. <paramref name="meterFactory"/> is used when the host has one (any
    /// ASP.NET Core or <c>AddMetrics()</c> host does), so the meter participates in that host's metrics
    /// configuration; otherwise one is created and owned here. Either way it carries the same name, which is
    /// all a collector subscribes to — and core takes no dependency to get it.
    /// </summary>
    internal CronnerInstrumentation(IMeterFactory? meterFactory, CronnerSchedulerDiagnostics diagnostics)
    {
        _ownsMeter = meterFactory is null;
        _meter = meterFactory?.Create(CronnerDiagnostics.MeterName, Version)
            ?? new Meter(CronnerDiagnostics.MeterName, Version);
        _activitySource = new ActivitySource(CronnerDiagnostics.ActivitySourceName, Version);

        _started = _meter.CreateCounter<long>(
            "cronner.jobs.started", "{job}", "Executions started.");
        _completed = _meter.CreateCounter<long>(
            "cronner.jobs.completed", "{job}", "Executions that finished without throwing.");
        _failed = _meter.CreateCounter<long>(
            "cronner.jobs.failed", "{job}", "Executions whose job body threw.");
        _cancelled = _meter.CreateCounter<long>(
            "cronner.jobs.cancelled", "{job}", "Executions that were cancelled or abandoned.");
        _retries = _meter.CreateCounter<long>(
            "cronner.jobs.retries", "{job}", "Retry attempts scheduled after a failure.");
        _misfired = _meter.CreateCounter<long>(
            "cronner.jobs.misfired", "{job}", "Occurrences that came due later than the misfire threshold.");
        _locksAcquired = _meter.CreateCounter<long>(
            "cronner.locks.acquired", "{lock}", "Execution locks claimed by this instance.");
        _locksLost = _meter.CreateCounter<long>(
            "cronner.locks.lost", "{lock}", "Execution locks lost or left unconfirmable mid-run.");
        _duration = _meter.CreateHistogram<double>(
            "cronner.jobs.duration", "s", "How long an execution took.");

        // The one metric that answers "is my scheduler keeping up?" — everything else tells you what ran, this
        // tells you how late it was. Measured from the occurrence's due time to the moment the body starts.
        _schedulingDelay = _meter.CreateHistogram<double>(
            "cronner.jobs.scheduling.delay", "s", "How long after its due time an execution actually started.");

        _meter.CreateObservableUpDownCounter(
            "cronner.jobs.running", () => (long)diagnostics.RunningCount, "{job}",
            "Executions in flight on this instance right now.");
    }

    /// <summary>
    /// Opens the span covering one execution attempt and makes it the ambient activity, so the spans the job
    /// body produces itself (EF Core, HttpClient, …) nest underneath it.
    /// </summary>
    internal Activity? StartRun(CronnerJob job, CronnerRunState runState, string owner)
    {
        var activity = _activitySource.StartActivity(
            $"{CronnerDiagnostics.ExecuteActivityName} {job.Id}", ActivityKind.Internal);

        if (activity is null)
            return null;

        activity.SetTag(CronnerDiagnostics.Tags.TaskId, job.Id);
        activity.SetTag(CronnerDiagnostics.Tags.ExecutionId, runState.ExecutionId);
        activity.SetTag(CronnerDiagnostics.Tags.Attempt, runState.Attempt);
        activity.SetTag(CronnerDiagnostics.Tags.MaxAttempts, runState.MaxAttempts);
        activity.SetTag(CronnerDiagnostics.Tags.JobKind, JobKind(job));
        activity.SetTag(CronnerDiagnostics.Tags.Owner, owner);
        return activity;
    }

    /// <summary>Records that an execution began, and how late it was against its due time.</summary>
    internal void RunStarted(CronnerJob job, DateTimeOffset dueTime)
    {
        var tags = TaskTags(job);
        _started.Add(1, tags);

        var delay = (DateTimeOffset.UtcNow - dueTime).TotalSeconds;
        _schedulingDelay.Record(delay < 0 ? 0 : delay, tags);
    }

    /// <summary>Records how an execution ended, its duration, and the span's status.</summary>
    internal void RunFinished(CronnerJob job, string outcome, TimeSpan duration, Exception? failure, Activity? activity)
    {
        var taskId = new KeyValuePair<string, object?>(CronnerDiagnostics.Tags.TaskId, job.Id);
        var outcomeTag = new KeyValuePair<string, object?>(CronnerDiagnostics.Tags.Outcome, outcome);

        _duration.Record(duration.TotalSeconds, taskId, outcomeTag);

        var counter = outcome switch
        {
            CronnerDiagnostics.Outcomes.Success => _completed,
            CronnerDiagnostics.Outcomes.Failed => _failed,
            _ => _cancelled,   // cancelled and lock_lost are both "did not complete on purpose"
        };
        counter.Add(1, taskId, new KeyValuePair<string, object?>(CronnerDiagnostics.Tags.JobKind, JobKind(job)));

        if (activity is null)
            return;

        activity.SetTag(CronnerDiagnostics.Tags.Outcome, outcome);
        if (failure is not null)
        {
            activity.AddException(failure);
            activity.SetStatus(ActivityStatusCode.Error, failure.Message);
        }
        else if (outcome != CronnerDiagnostics.Outcomes.Success)
        {
            activity.SetStatus(ActivityStatusCode.Error, outcome);
        }
    }

    /// <summary>Records that a retry attempt was scheduled after a failure.</summary>
    internal void RetryScheduled(CronnerJob job) => _retries.Add(1, TaskTags(job));

    /// <summary>Records that an occurrence was late enough to count as a misfire, and how it was handled.</summary>
    internal void Misfired(CronnerJob job, MisfirePolicy policy) =>
        _misfired.Add(1,
            new KeyValuePair<string, object?>(CronnerDiagnostics.Tags.TaskId, job.Id),
            new KeyValuePair<string, object?>(CronnerDiagnostics.Tags.MisfirePolicy, policy.ToString()));

    /// <summary>Records that this instance claimed a task's execution lock.</summary>
    internal void LockAcquired(CronnerJob job) => _locksAcquired.Add(1, TaskTags(job));

    /// <summary>Records that a run lost its execution lock, or could no longer confirm it.</summary>
    internal void LockLost(CronnerJob job) => _locksLost.Add(1, TaskTags(job));

    public void Dispose()
    {
        // A factory-created meter belongs to the factory; only dispose the one we made ourselves.
        if (_ownsMeter)
            _meter.Dispose();

        _activitySource.Dispose();
    }

    private static KeyValuePair<string, object?> TaskTags(CronnerJob job) =>
        new(CronnerDiagnostics.Tags.TaskId, job.Id);

    private static string JobKind(CronnerJob job) =>
        job.Kind == CronnerJobKind.OneOff ? "oneoff" : "recurring";
}
