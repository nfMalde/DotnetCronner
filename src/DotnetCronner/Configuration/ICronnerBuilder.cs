using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>
/// The fluent configuration surface for DotnetCronner, used from both
/// <c>services.AddDotnetCronner(...)</c> and <c>app.UseDotnetCronner(...)</c>.
/// </summary>
public interface ICronnerBuilder
{
    /// <summary>The underlying service collection (available when configuring at <c>AddDotnetCronner</c> time).</summary>
    IServiceCollection Services { get; }

    /// <summary>The mutable runtime options.</summary>
    CronnerOptions Options { get; }

    /// <summary>The store holder, used by store/cache extension packages to plug in their implementations.</summary>
    CronnerStoreHolder StoreHolder { get; }

    /// <summary>
    /// Uses the given custom <see cref="ICronnerStore"/> implementation as the backing store. The
    /// instance is resolved from DI when available, otherwise created via its constructor.
    /// Mutually exclusive with the Redis / EF Core store extensions.
    /// </summary>
    ICronnerBuilder UseStore<TStore>() where TStore : class, ICronnerStore;

    /// <summary>Enables a second-level cache in front of the store.</summary>
    ICronnerBuilder UseSecondLevelCache(Action<ICronnerCacheBuilder> configure);

    /// <summary>
    /// Turns off automatic discovery of <see cref="CronnerTaskAttribute"/> tasks in the entry assembly.
    /// Only tasks registered explicitly (via <c>Sched</c>) or opted in through
    /// <see cref="AutoDiscoverFromAssembly(Type[])"/> / <see cref="AutoDiscoverFromType"/> are registered.
    /// </summary>
    ICronnerBuilder DisableAutoDiscovery();

    /// <summary>Discovers attribute tasks in the assemblies that contain the given marker types.</summary>
    ICronnerBuilder AutoDiscoverFromAssembly(params Type[] assemblyMarkerTypes);

    /// <summary>Discovers attribute tasks in the given assemblies.</summary>
    ICronnerBuilder AutoDiscoverFromAssembly(params Assembly[] assemblies);

    /// <summary>
    /// Restricts attribute discovery to types assignable to one of the given interfaces or base classes.
    /// Applies across every scanned assembly; combine with <see cref="AutoDiscoverFromAssembly(Type[])"/>
    /// to also choose which assemblies are scanned.
    /// </summary>
    ICronnerBuilder AutoDiscoverFromType(params Type[] baseTypesOrInterfaces);

    /// <summary>Adjusts the runtime options.</summary>
    ICronnerBuilder Configure(Action<CronnerOptions> configure);

    /// <summary>
    /// Runs tasks against a dedicated, isolated service provider built from <paramref name="configure"/>
    /// instead of the application's provider. The discovered job classes are registered into it with
    /// <paramref name="jobLifetime"/> (Scoped by default). A fresh scope is still created per run.
    /// </summary>
    ICronnerBuilder WithDedicatedDI(
        Action<IServiceCollection> configure, ServiceLifetime jobLifetime = ServiceLifetime.Scoped);

    /// <summary>
    /// Pins how often a running task's lock is renewed and <c>OnKeepAlive</c> fires, independently of
    /// <see cref="CronnerOptions.LockTtl"/> (default cadence is <c>LockTtl</c>/2). Keep it below the TTL.
    /// </summary>
    ICronnerBuilder WithKeepAliveInterval(TimeSpan interval);

    /// <summary>
    /// Keeps only the newest <paramref name="keepNewest"/> finished one-off (enqueued) instances per
    /// definition; older ones are pruned after each completes. <c>0</c> disables retention (keep all).
    /// </summary>
    ICronnerBuilder WithOneOffRetention(int keepNewest);

    /// <summary>Adds a global lifecycle/lock hook instance.</summary>
    ICronnerBuilder AddHook(ICronnerTaskHook hook);

    /// <summary>Registers a global hook type, resolved from the hook's own scope per invocation.</summary>
    ICronnerBuilder AddHook<THook>() where THook : class, ICronnerTaskHook;

    /// <summary>Adds a global callback that runs immediately before each task executes.</summary>
    ICronnerBuilder OnStart(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global start hook that invokes a method on <typeparamref name="THook"/> (with <c>HasParam</c> support).</summary>
    ICronnerBuilder OnStart<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global start hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnStart<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs after a task completes successfully.</summary>
    ICronnerBuilder OnSuccess(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global success hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnSuccess<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global success hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnSuccess<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs after a task throws (see <see cref="CronnerTaskContext.Exception"/>).</summary>
    ICronnerBuilder OnFail(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global fail hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnFail<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global fail hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnFail<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs after a task is cancelled.</summary>
    ICronnerBuilder OnCancel(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global cancel hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnCancel<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global cancel hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnCancel<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task's execution lock is claimed.</summary>
    ICronnerBuilder OnLockAcquire(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global lock-acquire hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnLockAcquire<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global lock-acquire hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnLockAcquire<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs each time a task's execution lock is renewed (keepalive).</summary>
    ICronnerBuilder OnKeepAlive(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global keepalive hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnKeepAlive<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global keepalive hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnKeepAlive<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task's execution lock is released.</summary>
    ICronnerBuilder OnLockRelease(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global lock-release hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnLockRelease<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global lock-release hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnLockRelease<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task's execution lock is lost mid-run.</summary>
    ICronnerBuilder OnLockLost(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global lock-lost hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnLockLost<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global lock-lost hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnLockLost<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task reports total progress.</summary>
    ICronnerBuilder OnTotalProgressChange(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global total-progress hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnTotalProgressChange<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global total-progress hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnTotalProgressChange<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task opens a progress scope.</summary>
    ICronnerBuilder OnProgressScopeOpened(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global scope-opened hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnProgressScopeOpened<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global scope-opened hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnProgressScopeOpened<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task reports progress to a scope.</summary>
    ICronnerBuilder OnScopeProgress(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global scope-progress hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnScopeProgress<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global scope-progress hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnScopeProgress<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Adds a global callback that runs when a task closes a progress scope.</summary>
    ICronnerBuilder OnProgressScopeClosed(Func<CronnerTaskContext, Task> handler);

    /// <summary>Adds a global scope-closed hook that invokes a method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnProgressScopeClosed<THook>(Expression<Action<THook>> call);

    /// <summary>Adds a global scope-closed hook that invokes an async method on <typeparamref name="THook"/>.</summary>
    ICronnerBuilder OnProgressScopeClosed<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Schedules a synchronous task from a lambda expression.</summary>
    ICronnerBuilder Sched<TJob>(Expression<Action<TJob>> call, Action<ICronnerScheduleOptions> options);

    /// <summary>Schedules a synchronous task from a lambda expression using the given cron string.</summary>
    ICronnerBuilder Sched<TJob>(Expression<Action<TJob>> call, string cronString);

    /// <summary>Schedules an asynchronous task from a lambda expression (no CS4014 at the call site).</summary>
    ICronnerBuilder Sched<TJob>(Expression<Func<TJob, Task>> call, Action<ICronnerScheduleOptions> options);

    /// <summary>Schedules an asynchronous task from a lambda expression using the given cron string.</summary>
    ICronnerBuilder Sched<TJob>(Expression<Func<TJob, Task>> call, string cronString);
}
