using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class ExecutionHistoryTests
{
    [Fact]
    public void Duration_Is_Null_While_Running_Then_The_Elapsed_Time_Once_Finished()
    {
        var started = DateTimeOffset.UtcNow;
        var run = new CronnerJobExecution { Id = "e", JobId = "j", StartedAt = started };

        run.Duration.ShouldBeNull();                 // no FinishedAt yet → still running

        run.FinishedAt = started.AddSeconds(3);
        run.Duration.ShouldBe(TimeSpan.FromSeconds(3));
    }

    public sealed class Signals
    {
        public int Runs;
        public bool Throw;
        public TaskCompletionSource Ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class HistoryJob(Signals signals)
    {
        public void Run(CancellationToken ct)
        {
            Interlocked.Increment(ref signals.Runs);
            var done = signals.Ran;
            if (signals.Throw)
            {
                done.TrySetResult();
                throw new InvalidOperationException("boom");
            }

            done.TrySetResult();
        }
    }

    private static IHost Build(Signals signals, Action<ICronnerBuilder> extra) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(signals);
                services.AddSingleton<HistoryJob>();
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner.Sched<HistoryJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("hist"));
                    extra(cronner);
                });
            })
            .Build();

    private static async Task<CronnerJobExecution?> WaitForFinishedAsync(ICronnerClient client, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var run = (await client.GetExecutionsAsync(id)).FirstOrDefault(r => r.Status != JobExecutionStatus.Running);
            if (run is not null)
                return run;
            await Task.Delay(50);
        }

        return null;
    }

    [Fact]
    public async Task Records_A_Succeeded_Run()
    {
        var signals = new Signals();
        using var host = Build(signals, c => c.WithExecutionHistory(10));
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            await client.TriggerNowAsync("hist");
            (await Task.WhenAny(signals.Ran.Task, Task.Delay(5000))).ShouldBeSameAs(signals.Ran.Task);

            var run = await WaitForFinishedAsync(client, "hist");
            run.ShouldNotBeNull();
            run!.JobId.ShouldBe("hist");
            run.Status.ShouldBe(JobExecutionStatus.Succeeded);
            run.Attempt.ShouldBe(1);
            run.FinishedAt.ShouldNotBeNull();
            run.Error.ShouldBeNull();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Records_A_Failed_Run_With_Its_Error()
    {
        var signals = new Signals { Throw = true };
        using var host = Build(signals, c => c.WithExecutionHistory(10));
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            await client.TriggerNowAsync("hist");
            (await Task.WhenAny(signals.Ran.Task, Task.Delay(5000))).ShouldBeSameAs(signals.Ran.Task);

            var run = await WaitForFinishedAsync(client, "hist");
            run.ShouldNotBeNull();
            run!.Status.ShouldBe(JobExecutionStatus.Failed);
            run.Error.ShouldNotBeNull();
            run.Error!.ShouldContain("boom");
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Disabled_By_Default_Records_Nothing()
    {
        var signals = new Signals();
        using var host = Build(signals, _ => { }); // no WithExecutionHistory
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            await client.TriggerNowAsync("hist");
            (await Task.WhenAny(signals.Ran.Task, Task.Delay(5000))).ShouldBeSameAs(signals.Ran.Task);
            await Task.Delay(200); // give any (unexpected) write a chance to land

            (await client.GetExecutionsAsync("hist")).ShouldBeEmpty();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Retention_Keeps_Only_The_Newest_Per_Task()
    {
        var signals = new Signals();
        using var host = Build(signals, c => c.WithExecutionHistory(2));
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<ICronnerClient>();
            for (var i = 0; i < 5; i++)
            {
                signals.Arm();
                await client.TriggerNowAsync("hist");
                (await Task.WhenAny(signals.Ran.Task, Task.Delay(5000))).ShouldBeSameAs(signals.Ran.Task);
                await Task.Delay(80); // let the finalize + prune settle before the next trigger
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            IReadOnlyList<CronnerJobExecution> runs;
            do
            {
                await Task.Delay(100);
                runs = await client.GetExecutionsAsync("hist", 100);
            }
            while (runs.Count > 2 && DateTime.UtcNow < deadline);

            signals.Runs.ShouldBe(5);              // all five actually ran
            runs.Count.ShouldBeLessThanOrEqualTo(2); // but history is capped
            runs.Count.ShouldBeGreaterThan(0);
        }
        finally { await host.StopAsync(); }
    }
}
