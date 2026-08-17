using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCronner;

/// <summary>Default implementation of <see cref="ICronnerBuilder"/>.</summary>
internal sealed class CronnerBuilder : ICronnerBuilder
{
    private readonly CronnerRegistry _registry;
    private readonly CronnerHookRegistry _hooks;
    private readonly CronnerJobServices _jobServices;

    public CronnerBuilder(
        IServiceCollection services, CronnerOptions options, CronnerStoreHolder storeHolder,
        CronnerRegistry registry, CronnerHookRegistry hooks, CronnerJobServices jobServices)
    {
        Services = services;
        Options = options;
        StoreHolder = storeHolder;
        _registry = registry;
        _hooks = hooks;
        _jobServices = jobServices;
    }

    public IServiceCollection Services { get; }

    public CronnerOptions Options { get; }

    public CronnerStoreHolder StoreHolder { get; }

    public ICronnerBuilder UseStore<TStore>() where TStore : class, ICronnerStore
    {
        StoreHolder.ConfigureStore(
            sp => ActivatorUtilities.GetServiceOrCreateInstance<TStore>(sp),
            $"UseStore<{typeof(TStore).Name}>()");
        return this;
    }

    public ICronnerBuilder UseSecondLevelCache(Action<ICronnerCacheBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var cacheBuilder = new CronnerCacheBuilder();
        configure(cacheBuilder);
        if (cacheBuilder.Factory is null)
            throw new InvalidOperationException(
                "UseSecondLevelCache requires a cache provider, e.g. cache.UseRedisCacheProvider(...) or cache.UseCacheProvider<T>().");

        StoreHolder.ConfigureCache(cacheBuilder.Factory);
        return this;
    }

    public ICronnerBuilder DisableAutoDiscovery()
    {
        Options.ScanEntryAssembly = false;
        return this;
    }

    public ICronnerBuilder AutoDiscoverFromAssembly(params Type[] assemblyMarkerTypes)
    {
        ArgumentNullException.ThrowIfNull(assemblyMarkerTypes);
        foreach (var type in assemblyMarkerTypes)
            if (type is not null && !Options.AdditionalAssemblies.Contains(type.Assembly))
                Options.AdditionalAssemblies.Add(type.Assembly);
        return this;
    }

    public ICronnerBuilder AutoDiscoverFromAssembly(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
            if (assembly is not null && !Options.AdditionalAssemblies.Contains(assembly))
                Options.AdditionalAssemblies.Add(assembly);
        return this;
    }

    public ICronnerBuilder AutoDiscoverFromType(params Type[] baseTypesOrInterfaces)
    {
        ArgumentNullException.ThrowIfNull(baseTypesOrInterfaces);
        foreach (var type in baseTypesOrInterfaces)
            if (type is not null && !Options.DiscoveryTypeFilters.Contains(type))
                Options.DiscoveryTypeFilters.Add(type);
        return this;
    }

    public ICronnerBuilder Configure(Action<CronnerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }

    public ICronnerBuilder WithDedicatedDI(
        Action<IServiceCollection> configure, ServiceLifetime jobLifetime = ServiceLifetime.Scoped)
    {
        _jobServices.UseDedicated(configure, jobLifetime);
        return this;
    }

    public ICronnerBuilder WithKeepAliveInterval(TimeSpan interval)
    {
        Options.KeepAliveInterval = interval;
        return this;
    }

    public ICronnerBuilder WithOneOffRetention(int keepNewest)
    {
        Options.OneOffRetentionCount = Math.Max(0, keepNewest);
        return this;
    }

    public ICronnerBuilder Sched<TJob>(Expression<Action<TJob>> call, Action<ICronnerScheduleOptions> options) =>
        ScheduleCore(typeof(TJob), call, options);

    public ICronnerBuilder Sched<TJob>(Expression<Action<TJob>> call, string cronString) =>
        ScheduleCore(typeof(TJob), call, o => o.WithCron(cronString));

    public ICronnerBuilder Sched<TJob>(Expression<Func<TJob, Task>> call, Action<ICronnerScheduleOptions> options) =>
        ScheduleCore(typeof(TJob), call, options);

    public ICronnerBuilder Sched<TJob>(Expression<Func<TJob, Task>> call, string cronString) =>
        ScheduleCore(typeof(TJob), call, o => o.WithCron(cronString));

    private ICronnerBuilder ScheduleCore(Type jobType, LambdaExpression call, Action<ICronnerScheduleOptions> options)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(options);

        var scheduleOptions = new CronnerScheduleOptions();
        options(scheduleOptions);

        var (method, arguments) = CronnerExpressionParser.Parse(call);
        var id = string.IsNullOrWhiteSpace(scheduleOptions.Id)
            ? CronnerJobNaming.GetDefaultId(method)
            : scheduleOptions.Id;

        CronnerCronGuard.Validate(id, scheduleOptions.CronString);

        _registry.Add(new CronnerJobDescriptor(
            id, CronnerJobNaming.GetName(method), scheduleOptions.CronString, jobType, method, arguments,
            scheduleOptions.Priority, scheduleOptions.Concurrency, scheduleOptions.Description, scheduleOptions.Hooks));
        return this;
    }

    public ICronnerBuilder AddHook(ICronnerTaskHook hook)
    {
        _hooks.Add(hook);
        return this;
    }

    public ICronnerBuilder AddHook<THook>() where THook : class, ICronnerTaskHook =>
        // Routed through the registry (not DI) so it also works when configured via app.UseDotnetCronner.
        AddHook(CronnerHookFactory.FromType(typeof(THook)));

    public ICronnerBuilder OnStart(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Start, handler);
    public ICronnerBuilder OnStart<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Start, typeof(THook), call);
    public ICronnerBuilder OnStart<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Start, typeof(THook), call);

    public ICronnerBuilder OnSuccess(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Success, handler);
    public ICronnerBuilder OnSuccess<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Success, typeof(THook), call);
    public ICronnerBuilder OnSuccess<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Success, typeof(THook), call);

    public ICronnerBuilder OnFail(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Fail, handler);
    public ICronnerBuilder OnFail<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Fail, typeof(THook), call);
    public ICronnerBuilder OnFail<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Fail, typeof(THook), call);

    public ICronnerBuilder OnCancel(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Cancel, handler);
    public ICronnerBuilder OnCancel<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Cancel, typeof(THook), call);
    public ICronnerBuilder OnCancel<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Cancel, typeof(THook), call);

    public ICronnerBuilder OnLockAcquire(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.LockAcquire, handler);
    public ICronnerBuilder OnLockAcquire<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.LockAcquire, typeof(THook), call);
    public ICronnerBuilder OnLockAcquire<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.LockAcquire, typeof(THook), call);

    public ICronnerBuilder OnKeepAlive(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.KeepAlive, handler);
    public ICronnerBuilder OnKeepAlive<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.KeepAlive, typeof(THook), call);
    public ICronnerBuilder OnKeepAlive<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.KeepAlive, typeof(THook), call);

    public ICronnerBuilder OnLockRelease(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.LockRelease, handler);
    public ICronnerBuilder OnLockRelease<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.LockRelease, typeof(THook), call);
    public ICronnerBuilder OnLockRelease<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.LockRelease, typeof(THook), call);

    public ICronnerBuilder OnLockLost(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.LockLost, handler);
    public ICronnerBuilder OnLockLost<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.LockLost, typeof(THook), call);
    public ICronnerBuilder OnLockLost<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.LockLost, typeof(THook), call);

    public ICronnerBuilder OnTotalProgressChange(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.TotalProgressChange, handler);
    public ICronnerBuilder OnTotalProgressChange<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.TotalProgressChange, typeof(THook), call);
    public ICronnerBuilder OnTotalProgressChange<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.TotalProgressChange, typeof(THook), call);

    public ICronnerBuilder OnProgressScopeOpened(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.ProgressScopeOpened, handler);
    public ICronnerBuilder OnProgressScopeOpened<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.ProgressScopeOpened, typeof(THook), call);
    public ICronnerBuilder OnProgressScopeOpened<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.ProgressScopeOpened, typeof(THook), call);

    public ICronnerBuilder OnScopeProgress(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.ScopeProgress, handler);
    public ICronnerBuilder OnScopeProgress<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.ScopeProgress, typeof(THook), call);
    public ICronnerBuilder OnScopeProgress<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.ScopeProgress, typeof(THook), call);

    public ICronnerBuilder OnProgressScopeClosed(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.ProgressScopeClosed, handler);
    public ICronnerBuilder OnProgressScopeClosed<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.ProgressScopeClosed, typeof(THook), call);
    public ICronnerBuilder OnProgressScopeClosed<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.ProgressScopeClosed, typeof(THook), call);

    private ICronnerBuilder Delegate(CronnerHookEvent hookEvent, Func<CronnerTaskContext, Task> handler) =>
        AddHook(CronnerHookFactory.FromDelegate(hookEvent, handler));

    private ICronnerBuilder Expr(CronnerHookEvent hookEvent, Type targetType, LambdaExpression call) =>
        AddHook(CronnerHookFactory.FromExpression(hookEvent, targetType, call));
}
