using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DotnetCronner.Tests;

public class CronValidationTests
{
    public sealed class Sample
    {
        public void Run(CancellationToken ct) { }
    }

    private static void Register(Action<ICronnerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddDotnetCronner(c =>
        {
            c.Configure(o => o.ScanEntryAssembly = false);
            configure(c);
        });
        services.BuildServiceProvider();
    }

    [Fact]
    public void InvalidCron_AtRegistration_ThrowsImmediately_NamingTheTask()
    {
        var ex = Should.Throw<CronFormatException>(() => Register(c => c.Sched<Sample>(
            x => x.Run(x.HasParam<CancellationToken>()),
            o => o.WithCron("not a cron").WithId("broken"))));

        ex.Message.ShouldContain("broken");
    }

    [Fact]
    public void ValidCron_AtRegistration_DoesNotThrow()
    {
        Should.NotThrow(() => Register(c => c.Sched<Sample>(
            x => x.Run(x.HasParam<CancellationToken>()),
            o => o.WithCron("*/5 * * * *").WithId("ok"))));
    }

    [Fact]
    public void ManualTask_NoCron_IsAllowed()
    {
        Should.NotThrow(() => Register(c => c.Sched<Sample>(
            x => x.Run(x.HasParam<CancellationToken>()),
            o => o.WithId("manual"))));
    }
}
