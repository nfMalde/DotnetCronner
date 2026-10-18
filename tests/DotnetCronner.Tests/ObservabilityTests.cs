using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

/// <summary>
/// Observability: the metrics the scheduler publishes, the span it opens around each execution (and that
/// the job body sees as its ambient activity), the live scheduler diagnostics, and the health check built
/// on top of them.
/// </summary>
public class ObservabilityTests
{
    // ---- Metrics ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_Successful_Run_Counts_As_Started_And_Completed_With_A_Duration()
    {
        using var metrics = new MetricRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnSuccess(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        await RunOnceAsync(host, seen);

        metrics.Count("cronner.jobs.started").ShouldBe(1);
        metrics.Count("cronner.jobs.completed").ShouldBe(1);
        metrics.Count("cronner.jobs.failed").ShouldBe(0);
        metrics.Tag("cronner.jobs.started", CronnerDiagnostics.Tags.TaskId).ShouldBe("job");
        metrics.Tag("cronner.jobs.completed", CronnerDiagnostics.Tags.Outcome).ShouldBeNull("the outcome rides on the duration histogram");
        metrics.Tag("cronner.jobs.duration", CronnerDiagnostics.Tags.Outcome).ShouldBe(CronnerDiagnostics.Outcomes.Success);
        metrics.Values("cronner.jobs.duration").ShouldHaveSingleItem();
        metrics.Values("cronner.jobs.scheduling.delay").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_Failing_Run_Counts_As_Failed_And_Its_Retry_Is_Counted()
    {
        using var metrics = new MetricRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(ctx => { if (!ctx.WillRetry) seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(), o => o
                .WithId("job")
                .WithRetryPolicy(p => p.MaxAttempts(2).Immediate())));

        await RunOnceAsync(host, seen);

        metrics.Count("cronner.jobs.started").ShouldBe(2, "the first attempt plus its retry");
        metrics.Count("cronner.jobs.failed").ShouldBe(2);
        metrics.Count("cronner.jobs.completed").ShouldBe(0);
        metrics.Count("cronner.jobs.retries").ShouldBe(1, "one retry was scheduled, after the first attempt");
        metrics.Tag("cronner.jobs.duration", CronnerDiagnostics.Tags.Outcome).ShouldBe(CronnerDiagnostics.Outcomes.Failed);
    }

    [Fact]
    public async Task Claiming_A_Task_Counts_As_A_Lock_Acquired()
    {
        using var metrics = new MetricRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnSuccess(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        await RunOnceAsync(host, seen);

        metrics.Count("cronner.locks.acquired").ShouldBe(1);
        metrics.Count("cronner.locks.lost").ShouldBe(0);
    }

    [Fact]
    public async Task Running_Executions_Are_Observable_While_A_Job_Is_In_Flight()
    {
        using var metrics = new MetricRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnStart(_ => { seen.Started.TrySetResult(true); return Task.CompletedTask; })
            .OnSuccess(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<GatedJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("job")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Started.Task, 10);
            await Task.Delay(200);   // let the body get going

            metrics.Observe();
            metrics.LastValue("cronner.jobs.running").ShouldBe(1);
            host.Services.GetRequiredService<CronnerSchedulerDiagnostics>().RunningCount.ShouldBe(1);

            seen.Gate.TrySetResult(true);   // let the job finish
            await Within(seen.Done.Task, 10);
            await Task.Delay(200);

            metrics.Observe();
            metrics.LastValue("cronner.jobs.running").ShouldBe(0, "the gauge comes back down when the run ends");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---- Tracing ---------------------------------------------------------------------------------

    [Fact]
    public async Task Each_Execution_Produces_A_Span_Tagged_With_The_Task_And_Attempt()
    {
        using var spans = new SpanRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnSuccess(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        await RunOnceAsync(host, seen);

        var span = spans.Finished.ShouldHaveSingleItem();
        span.DisplayName.ShouldBe($"{CronnerDiagnostics.ExecuteActivityName} job");
        span.GetTagItem(CronnerDiagnostics.Tags.TaskId).ShouldBe("job");
        span.GetTagItem(CronnerDiagnostics.Tags.Attempt).ShouldBe(1);
        span.GetTagItem(CronnerDiagnostics.Tags.Outcome).ShouldBe(CronnerDiagnostics.Outcomes.Success);
        span.GetTagItem(CronnerDiagnostics.Tags.JobKind).ShouldBe("recurring");
        span.GetTagItem(CronnerDiagnostics.Tags.ExecutionId).ShouldBe(seen.ExecutionId);
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task The_Span_Is_The_Ambient_Activity_While_The_Job_Body_Runs()
    {
        // This is what makes the whole thing worth having: spans the job emits itself — an EF Core query, an
        // outbound HTTP call — nest under the execution instead of floating free.
        using var spans = new SpanRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnSuccess(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        await RunOnceAsync(host, seen);

        seen.AmbientActivityName.ShouldBe($"{CronnerDiagnostics.ExecuteActivityName} job");
        seen.ChildParentId.ShouldNotBeNull();
        var span = spans.Finished.First(a => a.DisplayName.StartsWith(CronnerDiagnostics.ExecuteActivityName));
        seen.ChildParentId.ShouldBe(span.Id, "a span the job opens is a child of the execution span");
    }

    [Fact]
    public async Task A_Failed_Run_Marks_Its_Span_As_An_Error()
    {
        using var spans = new SpanRecorder();
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(), o => o.WithId("job")));

        await RunOnceAsync(host, seen);

        var span = spans.Finished.ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe("boom");
        span.GetTagItem(CronnerDiagnostics.Tags.Outcome).ShouldBe(CronnerDiagnostics.Outcomes.Failed);
        span.Events.ShouldContain(e => e.Name == "exception");
    }

    [Fact]
    public void Nothing_Is_Produced_When_Nothing_Is_Listening()
    {
        // No listener attached: StartActivity returns null and the counters are inert, so instrumentation
        // costs an unobserved application nothing.
        using var host = BuildHost(new Seen(), cronner => cronner
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job")));

        var source = new ActivitySource(CronnerDiagnostics.ActivitySourceName);
        source.StartActivity("probe").ShouldBeNull();
        source.HasListeners().ShouldBeFalse();
    }

    // ---- Scheduler diagnostics -------------------------------------------------------------------

    [Fact]
    public async Task The_Scheduler_Reports_That_It_Started_And_Keeps_Polling()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job").WithCron("0 0 1 1 *")));

        var diagnostics = host.Services.GetRequiredService<CronnerSchedulerDiagnostics>();
        diagnostics.IsStarted.ShouldBeFalse();

        await host.StartAsync();
        try
        {
            diagnostics.IsStarted.ShouldBeTrue();

            await WaitUntil(() => diagnostics.PollCount > 0, 10);
            var first = diagnostics.LastPollUtc.ShouldNotBeNull();

            await WaitUntil(() => diagnostics.LastPollUtc > first, 10);
            diagnostics.LastPollUtc!.Value.ShouldBeGreaterThan(first, "the poll loop keeps turning with nothing to do");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---- Health check ----------------------------------------------------------------------------

    [Fact]
    public async Task The_Health_Check_Is_Healthy_While_The_Scheduler_Runs()
    {
        using var host = BuildHost(new Seen(), cronner => cronner
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job").WithCron("0 0 1 1 *")),
            services => services.AddHealthChecks().AddCronner());

        await host.StartAsync();
        try
        {
            var diagnostics = host.Services.GetRequiredService<CronnerSchedulerDiagnostics>();
            await WaitUntil(() => diagnostics.PollCount > 0, 10);

            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            report.Status.ShouldBe(HealthStatus.Healthy);
            report.Entries["dotnetcronner"].Data["started"].ShouldBe(true);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task The_Health_Check_Is_Unhealthy_Before_The_Scheduler_Starts()
    {
        using var host = BuildHost(new Seen(), cronner => cronner
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job").WithCron("0 0 1 1 *")),
            services => services.AddHealthChecks().AddCronner());

        // Never started — nothing will ever run, and the check must say so rather than report healthy.
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        report.Status.ShouldBe(HealthStatus.Unhealthy);
        report.Entries["dotnetcronner"].Description.ShouldNotBeNull().ShouldContain("has not started");
    }

    [Fact]
    public async Task The_Health_Check_Is_Unhealthy_When_The_Store_Cannot_Be_Reached()
    {
        using var host = BuildHost(new Seen(), cronner => cronner
            .UseStore<UnreachableStore>()
            .Sched<Job>(x => x.Run(x.HasParam<ICronnerJobContext>()), o => o.WithId("job").WithCron("0 0 1 1 *")),
            services =>
            {
                services.AddSingleton<UnreachableStore>();
                services.AddHealthChecks().AddCronner();
            });

        await host.StartAsync();
        try
        {
            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            report.Status.ShouldBe(HealthStatus.Unhealthy);
            report.Entries["dotnetcronner"].Description.ShouldNotBeNull().ShouldContain("store is unreachable");
            report.Entries["dotnetcronner"].Exception.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Failing_Job_Does_Not_Make_The_Application_Unhealthy()
    {
        // The scheduler is fine; a task threw. That is the application's problem to handle, not a reason to
        // have an orchestrator restart the process.
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnFail(_ => { seen.Done.TrySetResult(true); return Task.CompletedTask; })
            .Sched<FailingJob>(x => x.Run(), o => o.WithId("job")),
            services => services.AddHealthChecks().AddCronner());

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Done.Task, 10);

            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            report.Status.ShouldBe(HealthStatus.Healthy);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_Run_Past_The_Stalled_Threshold_Is_Degraded_Not_Unhealthy()
    {
        var seen = new Seen();
        using var host = BuildHost(seen, cronner => cronner
            .OnStart(_ => { seen.Started.TrySetResult(true); return Task.CompletedTask; })
            .Sched<SlowJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("job")),
            services => services.AddHealthChecks().AddCronner(o => o.StalledRunThreshold = TimeSpan.FromMilliseconds(100)));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Started.Task, 10);
            await Task.Delay(400);   // comfortably past the 100ms threshold

            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            report.Status.ShouldBe(HealthStatus.Degraded, "work is still moving; one run is merely slow");
            report.Entries["dotnetcronner"].Description.ShouldNotBeNull().ShouldContain("stalled-run threshold");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---- Harness ---------------------------------------------------------------------------------

    public sealed class Seen
    {
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ExecutionId;
        public string? AmbientActivityName;
        public string? ChildParentId;
    }

    public sealed class Job(Seen seen)
    {
        public void Run(ICronnerJobContext ctx)
        {
            seen.ExecutionId = ctx.ExecutionId;
            seen.AmbientActivityName = Activity.Current?.DisplayName;

            // Stand in for whatever the job really does that is itself instrumented.
            using var child = new ActivitySource("TestApp").StartActivity("job-work");
            seen.ChildParentId = Activity.Current?.ParentId;
        }
    }

    public sealed class FailingJob
    {
        public void Run() => throw new InvalidOperationException("boom");
    }

    /// <summary>Runs until the test opens the gate, so "a run is in flight" is a fact, not a race.</summary>
    public sealed class GatedJob(Seen seen)
    {
        public Task Run(CancellationToken cancellationToken) => seen.Gate.Task.WaitAsync(cancellationToken);
    }

    public sealed class SlowJob
    {
        public Task Run(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }

    /// <summary>
    /// A store that works normally except that it cannot be pinged — a database still holding the
    /// scheduler's seeded state but no longer answering, which is exactly what the probe exists to catch.
    /// </summary>
    public sealed class UnreachableStore : ICronnerStore
    {
        private readonly InMemoryCronnerStore _inner = new();

        public Task PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("the store is down"));

        public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<CronnerJob>> GetAsync(
            CronnerTaskState? state, int offset, int limit, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(state, offset, limit, cancellationToken);

        public Task UpsertAsync(CronnerJob job, CancellationToken cancellationToken = default) =>
            _inner.UpsertAsync(job, cancellationToken);

        public Task RemoveAsync(string id, CancellationToken cancellationToken = default) =>
            _inner.RemoveAsync(id, cancellationToken);

        public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(
            DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken cancellationToken = default) =>
            _inner.AcquireDueAsync(now, owner, lockTtl, max, cancellationToken);

        public Task<bool> RenewLockAsync(
            string id, string owner, DateTimeOffset lockedUntil, CancellationToken cancellationToken = default) =>
            _inner.RenewLockAsync(id, owner, lockedUntil, cancellationToken);
    }

    private static IHost BuildHost(Seen seen, Action<ICronnerBuilder> configure, Action<IServiceCollection>? services = null) =>
        new HostBuilder()
            .ConfigureServices(collection =>
            {
                collection.AddSingleton(seen);
                collection.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o =>
                    {
                        o.ScanEntryAssembly = false;
                        o.PollingInterval = TimeSpan.FromMilliseconds(50);
                    });
                    configure(cronner);
                });
                services?.Invoke(collection);
            })
            .Build();

    private static async Task RunOnceAsync(IHost host, Seen seen)
    {
        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("job");
            await Within(seen.Done.Task, 15);
            await Task.Delay(300);   // let the final metric and span land
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task Within<T>(Task<T> task, int seconds) =>
        (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)))).ShouldBeSameAs(task, "timed out");

    private static async Task WaitUntil(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }

        condition().ShouldBeTrue("timed out waiting for the condition");
    }

    /// <summary>Collects every measurement published on the DotnetCronner meter, from any host in this process.</summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();

        public MetricRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CronnerDiagnostics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            _listener.Start();
        }

        /// <summary>Polls the observable instruments (gauges report only when asked).</summary>
        public void Observe() => _listener.RecordObservableInstruments();

        public long Count(string instrument) => (long)Values(instrument).Sum();

        public IReadOnlyList<double> Values(string instrument) =>
            _measurements.Where(m => m.Instrument == instrument).Select(m => m.Value).ToArray();

        public double? LastValue(string instrument) =>
            _measurements.Where(m => m.Instrument == instrument).Select(m => (double?)m.Value).LastOrDefault();

        public object? Tag(string instrument, string tag) =>
            _measurements.Where(m => m.Instrument == instrument)
                .Select(m => m.Tags.GetValueOrDefault(tag))
                .FirstOrDefault(v => v is not null);

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copied = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copied[tag.Key] = tag.Value;

            _measurements.Enqueue((instrument.Name, value, copied));
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Collects every span published on the DotnetCronner activity source.</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _finished = new();

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                // "TestApp" stands in for the job's own instrumentation, so the nesting assertion is real.
                ShouldListenTo = source =>
                    source.Name == CronnerDiagnostics.ActivitySourceName || source.Name == "TestApp",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == CronnerDiagnostics.ActivitySourceName)
                        _finished.Enqueue(activity);
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Finished => _finished.ToArray();

        public void Dispose() => _listener.Dispose();
    }
}
