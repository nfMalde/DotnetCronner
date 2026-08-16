using DotnetCronner;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DotnetCronner.Tests;

public class SchedulingRegistrationTests
{
    public interface IFoo;

    public sealed class Sample
    {
        public void Do(string a, int b, IFoo foo, CancellationToken ct) { }

        public Task DoAsync(IFoo foo, CancellationToken ct) => Task.CompletedTask;
    }

    private static CronnerRegistry Register(Action<ICronnerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddDotnetCronner(c =>
        {
            c.Configure(o => o.ScanEntryAssembly = false);
            configure(c);
        });
        return services.BuildServiceProvider().GetRequiredService<CronnerRegistry>();
    }

    [Fact]
    public void HasParam_And_Literals_ProduceCorrectArgumentPlan()
    {
        var registry = Register(c => c.Sched<Sample>(
            x => x.Do("hello", 42, x.HasParam<IFoo>(), x.HasParam<CancellationToken>()),
            o => o.WithCron("* * * * *").WithId("t1")));

        registry.TryGet("t1", out var descriptor).ShouldBeTrue();
        descriptor!.Arguments.Count.ShouldBe(4);

        descriptor.Arguments[0].Kind.ShouldBe(CronnerArgumentKind.Literal);
        descriptor.Arguments[0].Value.ShouldBe("hello");

        descriptor.Arguments[1].Kind.ShouldBe(CronnerArgumentKind.Literal);
        descriptor.Arguments[1].Value.ShouldBe(42);

        descriptor.Arguments[2].Kind.ShouldBe(CronnerArgumentKind.Service);
        descriptor.Arguments[2].Type.ShouldBe(typeof(IFoo));

        descriptor.Arguments[3].Kind.ShouldBe(CronnerArgumentKind.CancellationToken);
    }

    [Fact]
    public void AsyncLambda_BindsToTaskOverload_AndRegisters()
    {
        // If this bound to the Action overload the discarded Task would raise CS4014, which the test
        // project treats as an error — so compiling proves the Func<TJob, Task> overload is selected.
        var registry = Register(c => c.Sched<Sample>(
            x => x.DoAsync(x.HasParam<IFoo>(), x.HasParam<CancellationToken>()),
            o => o.WithCron("* * * * *").WithId("async")));

        registry.TryGet("async", out _).ShouldBeTrue();
    }

    [Fact]
    public void DefaultId_IsFullyQualifiedName()
    {
        var registry = Register(c => c.Sched<Sample>(x => x.Do("a", 1, x.HasParam<IFoo>(), x.HasParam<CancellationToken>()), "* * * * *"));
        var expectedId = $"{typeof(Sample).FullName}.{nameof(Sample.Do)}";
        registry.TryGet(expectedId, out _).ShouldBeTrue();
    }

    [Fact]
    public void DuplicateId_Throws()
    {
        Should.Throw<DuplicateCronnerTaskException>(() => Register(c => c
            .Sched<Sample>(x => x.Do("a", 1, x.HasParam<IFoo>(), x.HasParam<CancellationToken>()), o => o.WithCron("* * * * *").WithId("dup"))
            .Sched<Sample>(x => x.Do("b", 2, x.HasParam<IFoo>(), x.HasParam<CancellationToken>()), o => o.WithCron("* * * * *").WithId("dup"))));
    }

    [Fact]
    public void HasParam_FactoryOverload_ProducesFactoryArgument_ThatResolvesFromProvider()
    {
        var registry = Register(c => c.Sched<Sample>(
            x => x.Do("a", 1, x.HasParam<IFoo>(sp => sp.GetRequiredService<IFoo>()), x.HasParam<CancellationToken>()),
            o => o.WithCron("* * * * *").WithId("factory")));

        registry.TryGet("factory", out var descriptor).ShouldBeTrue();
        var arg = descriptor!.Arguments[2];
        arg.Kind.ShouldBe(CronnerArgumentKind.Factory);
        arg.Type.ShouldBe(typeof(IFoo));

        var foo = new Foo();
        var services = new ServiceCollection();
        services.AddSingleton<IFoo>(foo);
        using var provider = services.BuildServiceProvider();

        arg.Factory!(provider).ShouldBeSameAs(foo);
    }

    private sealed class Foo : IFoo;

    [Fact]
    public void Priority_And_Concurrency_ArePropagated()
    {
        var registry = Register(c => c.Sched<Sample>(
            x => x.Do("a", 1, x.HasParam<IFoo>(), x.HasParam<CancellationToken>()),
            o => o.WithCron("* * * * *").WithId("p1").WithPrio(CronnerTaskPriority.Critical).WithConcurrency(CronnerConcurrencyMode.Queue)));

        registry.TryGet("p1", out var descriptor).ShouldBeTrue();
        descriptor!.Priority.ShouldBe(CronnerTaskPriority.Critical);
        descriptor.Concurrency.ShouldBe(CronnerConcurrencyMode.Queue);
    }
}
