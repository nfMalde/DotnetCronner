using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class Cronner003Tests
{
    // ---- shared test doubles -------------------------------------------------------------------

    public sealed record ImportPayload(int Count, string Source);

    public sealed class PayloadSink
    {
        public ImportPayload? Received { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Marker so attribute discovery only picks up this test's enqueue job, not every [CronnerTask] in the assembly.
    public interface IEnqueueMarker;

    public sealed class EnqueueJobs : IEnqueueMarker
    {
        [CronnerTask("enqueue:import", Description = "Imports a batch")]   // no cron = enqueue-only
        public Task RunAsync(ImportPayload payload, PayloadSink sink, CancellationToken ct)
        {
            sink.Received = payload;
            sink.Done.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static IHost BuildEnqueueHost(PayloadSink sink, Action<ICronnerBuilder>? extra = null) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(sink);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner.AutoDiscoverFromAssembly(typeof(EnqueueJobs)).AutoDiscoverFromType(typeof(IEnqueueMarker));
                    extra?.Invoke(cronner);
                });
            })
            .Build();

    // ---- §1.1 enqueue + payload ----------------------------------------------------------------

    [Fact]
    public async Task Enqueue_Delivers_Payload_And_Runs_Once()
    {
        var sink = new PayloadSink();
        using var host = BuildEnqueueHost(sink);
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            var instanceId = await client.EnqueueAsync("enqueue:import", new ImportPayload(42, "steam"));

            (await Task.WhenAny(sink.Done.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(sink.Done.Task);
            sink.Received.ShouldBe(new ImportPayload(42, "steam"));

            // The one-off instance ran once and is now finished (Completed), not rescheduled.
            var job = await client.GetTaskByIdAsync(instanceId);
            job.ShouldNotBeNull();
            job!.Kind.ShouldBe(CronnerJobKind.OneOff);
            job.DefinitionId.ShouldBe("enqueue:import");
            job.State.ShouldBe(CronnerTaskState.Completed);
            job.NextRunUtc.ShouldBeNull();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Enqueue_Retention_Keeps_Only_Newest()
    {
        var sink = new PayloadSink();
        using var host = BuildEnqueueHost(sink, c => c.WithOneOffRetention(2));
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            for (var i = 0; i < 5; i++)
            {
                sink.Done.Task.IsCompleted.ToString(); // no-op; each enqueue reuses the sink
                await client.EnqueueAsync("enqueue:import", new ImportPayload(i, "s"));
            }

            // Give the scheduler time to run all five and prune down to 2.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            IReadOnlyList<CronnerJob> oneOffs;
            do
            {
                await Task.Delay(150);
                oneOffs = (await client.GetTasksAsync(limit: 100))
                    .Where(j => j.Kind == CronnerJobKind.OneOff).ToArray();
            }
            while (oneOffs.Count(j => j.State == CronnerTaskState.Completed) > 2 && DateTime.UtcNow < deadline);

            oneOffs.Count(j => j.State == CronnerTaskState.Completed).ShouldBeLessThanOrEqualTo(2);
        }
        finally { await host.StopAsync(); }
    }

    // ---- §1.2 / §1.3 / §2.3 client API ---------------------------------------------------------

    [Fact]
    public async Task GetRegisteredTasks_Lists_Definitions_With_Description_And_IsManual()
    {
        var sink = new PayloadSink();
        using var host = BuildEnqueueHost(sink);
        await host.StartAsync();
        try
        {
            var registered = host.Services.GetRequiredService<ICronnerClient>().GetRegisteredTasks();
            var import = registered.SingleOrDefault(t => t.Id == "enqueue:import");
            import.ShouldNotBeNull();
            import!.Description.ShouldBe("Imports a batch");
            import.IsManual.ShouldBeTrue();          // no cron
            import.CronExpression.ShouldBeNull();
        }
        finally { await host.StopAsync(); }
    }

    // ---- §2.1 never-firing cron ----------------------------------------------------------------

    public sealed class NoopJob
    {
        public void Run(CancellationToken ct) { }
    }

    [Fact]
    public async Task NeverFiring_Cron_Is_Marked_Failed_Not_Silent()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddDotnetCronner(cronner =>
            {
                cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                // 31 February — parses cleanly, never occurs.
                cronner.Sched<NoopJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithCron("0 0 31 2 *").WithId("never"));
            }))
            .Build();

        await host.StartAsync();
        try
        {
            var job = await host.Services.GetRequiredService<ICronnerClient>().GetTaskByIdAsync("never");
            job.ShouldNotBeNull();
            job!.State.ShouldBe(CronnerTaskState.Failed);
            job.NextRunUtc.ShouldBeNull();
            job.LastError.ShouldNotBeNull();
        }
        finally { await host.StopAsync(); }
    }

    // ---- §3.1 terminal hook shares the job's scope ---------------------------------------------

    public sealed class ScopedBag { public string? Value { get; set; } }

    public sealed class ScopeJob
    {
        public void Run(ScopedBag bag) => bag.Value = "from-job";
    }

    [Fact]
    public async Task Terminal_Hook_Shares_Job_Scope()
    {
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddScoped<ScopedBag>();
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner
                        // The hook reads the scoped bag the job wrote — only equal if they share the scope.
                        .OnSuccess(ctx => { seen.TrySetResult(ctx.HasParam<ScopedBag>().Value); return Task.CompletedTask; })
                        .Sched<ScopeJob>(x => x.Run(x.HasParam<ScopedBag>()), o => o.WithId("scope"));
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().TriggerNowAsync("scope");

            (await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(seen.Task);
            (await seen.Task).ShouldBe("from-job");
        }
        finally { await host.StopAsync(); }
    }
}
