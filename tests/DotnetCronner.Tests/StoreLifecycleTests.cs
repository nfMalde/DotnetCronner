using System.Collections.Concurrent;
using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace DotnetCronner.Tests;

public class StoreLifecycleTests
{
    public sealed class OkJob
    {
        public void Run(CancellationToken ct) { }
    }

    public sealed class BoomJob
    {
        public void Run(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    // Records the store call sequence; OnStart/OnClose delegate around an in-memory store.
    private sealed class RecordingStore : ICronnerStore
    {
        private readonly InMemoryCronnerStore _inner = new();

        public ConcurrentQueue<string> Calls { get; } = new();
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnStartAsync(CronnerJob job, CancellationToken ct = default)
        {
            Calls.Enqueue("start");
            return Task.CompletedTask;
        }

        public Task OnCloseAsync(CronnerJob job, CancellationToken ct = default)
        {
            Calls.Enqueue("close");
            Closed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task UpsertAsync(CronnerJob job, CancellationToken ct = default)
        {
            Calls.Enqueue("upsert");
            return _inner.UpsertAsync(job, ct);
        }

        public Task<CronnerJob?> GetByIdAsync(string id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<CronnerJob>> GetAsync(CronnerTaskState? state, int offset, int limit, CancellationToken ct = default) => _inner.GetAsync(state, offset, limit, ct);
        public Task RemoveAsync(string id, CancellationToken ct = default) => _inner.RemoveAsync(id, ct);
        public Task<IReadOnlyList<CronnerJob>> AcquireDueAsync(DateTimeOffset now, string owner, TimeSpan lockTtl, int max, CancellationToken ct = default) => _inner.AcquireDueAsync(now, owner, lockTtl, max, ct);
        public Task<bool> RenewLockAsync(string id, string owner, DateTimeOffset lockedUntil, CancellationToken ct = default) => _inner.RenewLockAsync(id, owner, lockedUntil, ct);
    }

    private static IHost BuildHost(RecordingStore store, Action<ICronnerBuilder> configure) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(store);
                services.AddDotnetCronner(cronner =>
                {
                    cronner.Configure(o => { o.ScanEntryAssembly = false; o.PollingInterval = TimeSpan.FromMilliseconds(50); });
                    cronner.UseStore<RecordingStore>();
                    configure(cronner);
                });
            })
            .Build();

    [Fact]
    public async Task OnStart_Precedes_StoreActions_And_OnClose_Is_Last()
    {
        var store = new RecordingStore();
        using var host = BuildHost(store, cronner => cronner
            .Sched<OkJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("run")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("run");

            (await Task.WhenAny(store.Closed.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(store.Closed.Task);

            var calls = store.Calls.ToArray();
            calls[^1].ShouldBe("close");                 // OnClose is the very last store call

            var startIdx = Array.LastIndexOf(calls, "start");
            var closeIdx = Array.LastIndexOf(calls, "close");
            startIdx.ShouldBeGreaterThanOrEqualTo(0);
            startIdx.ShouldBeLessThan(closeIdx);          // OnStart before OnClose
            // The run's state writes happened between OnStart and OnClose.
            calls.Skip(startIdx + 1).Take(closeIdx - startIdx - 1).ShouldContain("upsert");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task OnClose_Runs_Even_When_Job_Fails()
    {
        var store = new RecordingStore();
        using var host = BuildHost(store, cronner => cronner
            .Sched<BoomJob>(x => x.Run(x.HasParam<CancellationToken>()), o => o.WithId("boom")));

        await host.StartAsync();
        try
        {
            await host.Services.GetRequiredService<ICronnerClient>().ScheduleTaskAsync("boom");

            (await Task.WhenAny(store.Closed.Task, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBeSameAs(store.Closed.Task);
            store.Calls.ShouldContain("start");
            store.Calls.ToArray()[^1].ShouldBe("close");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
