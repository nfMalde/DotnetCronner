using System.Linq.Expressions;

namespace DotnetCronner;

/// <summary>Options for a task scheduled via a <c>Sched</c> lambda.</summary>
public interface ICronnerScheduleOptions
{
    /// <summary>Sets the cron expression that drives the schedule.</summary>
    ICronnerScheduleOptions WithCron(string cronString);

    /// <summary>Overrides the task id. When not set, an id is derived from the target type and method.</summary>
    ICronnerScheduleOptions WithId(string id);

    /// <summary>Sets the task's execution priority. Defaults to <see cref="CronnerTaskPriority.Normal"/>.</summary>
    ICronnerScheduleOptions WithPrio(CronnerTaskPriority priority);

    /// <summary>
    /// Sets what happens when the task becomes due while a previous run is still executing. Defaults to
    /// <see cref="CronnerConcurrencyMode.DropAndForget"/>.
    /// </summary>
    ICronnerScheduleOptions WithConcurrency(CronnerConcurrencyMode mode);

    /// <summary>Sets a human-readable description, surfaced on <c>CronnerJobDescriptor</c> and admin listings.</summary>
    ICronnerScheduleOptions WithDescription(string description);

    /// <summary>Attaches a hook instance to this schedule only.</summary>
    ICronnerScheduleOptions WithHook(ICronnerTaskHook hook);

    /// <summary>Attaches a hook type to this schedule only; resolved from the hook's own scope per invocation.</summary>
    ICronnerScheduleOptions WithHook<THook>() where THook : class, ICronnerTaskHook;

    /// <summary>Runs before each execution of this task.</summary>
    ICronnerScheduleOptions OnStart(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs before each execution, invoking a method on <typeparamref name="THook"/> (with <c>HasParam</c> support).</summary>
    ICronnerScheduleOptions OnStart<THook>(Expression<Action<THook>> call);

    /// <summary>Runs before each execution, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnStart<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs after a successful execution of this task.</summary>
    ICronnerScheduleOptions OnSuccess(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs after a successful execution, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnSuccess<THook>(Expression<Action<THook>> call);

    /// <summary>Runs after a successful execution, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnSuccess<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs after this task throws.</summary>
    ICronnerScheduleOptions OnFail(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs after this task throws, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnFail<THook>(Expression<Action<THook>> call);

    /// <summary>Runs after this task throws, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnFail<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs after this task is cancelled.</summary>
    ICronnerScheduleOptions OnCancel(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs after this task is cancelled, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnCancel<THook>(Expression<Action<THook>> call);

    /// <summary>Runs after this task is cancelled, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnCancel<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when the execution lock for this task is claimed.</summary>
    ICronnerScheduleOptions OnLockAcquire(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs when the lock is claimed, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnLockAcquire<THook>(Expression<Action<THook>> call);

    /// <summary>Runs when the lock is claimed, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnLockAcquire<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs each time the execution lock is renewed (keepalive) while this task runs.</summary>
    ICronnerScheduleOptions OnKeepAlive(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs on each keepalive, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnKeepAlive<THook>(Expression<Action<THook>> call);

    /// <summary>Runs on each keepalive, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnKeepAlive<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when the execution lock for this task is released.</summary>
    ICronnerScheduleOptions OnLockRelease(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs when the lock is released, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnLockRelease<THook>(Expression<Action<THook>> call);

    /// <summary>Runs when the lock is released, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnLockRelease<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when the execution lock for this task is lost mid-run.</summary>
    ICronnerScheduleOptions OnLockLost(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs when the lock is lost, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnLockLost<THook>(Expression<Action<THook>> call);

    /// <summary>Runs when the lock is lost, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnLockLost<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when this task reports total progress.</summary>
    ICronnerScheduleOptions OnTotalProgressChange(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs on total progress, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnTotalProgressChange<THook>(Expression<Action<THook>> call);

    /// <summary>Runs on total progress, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnTotalProgressChange<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when this task opens a progress scope.</summary>
    ICronnerScheduleOptions OnProgressScopeOpened(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs when a scope opens, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnProgressScopeOpened<THook>(Expression<Action<THook>> call);

    /// <summary>Runs when a scope opens, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnProgressScopeOpened<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when this task reports progress to a scope.</summary>
    ICronnerScheduleOptions OnScopeProgress(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs on scope progress, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnScopeProgress<THook>(Expression<Action<THook>> call);

    /// <summary>Runs on scope progress, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnScopeProgress<THook>(Expression<Func<THook, Task>> call);

    /// <summary>Runs when this task closes a progress scope.</summary>
    ICronnerScheduleOptions OnProgressScopeClosed(Func<CronnerTaskContext, Task> handler);

    /// <summary>Runs when a scope closes, invoking a method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnProgressScopeClosed<THook>(Expression<Action<THook>> call);

    /// <summary>Runs when a scope closes, invoking an async method on <typeparamref name="THook"/>.</summary>
    ICronnerScheduleOptions OnProgressScopeClosed<THook>(Expression<Func<THook, Task>> call);
}

/// <summary>Default mutable implementation of <see cref="ICronnerScheduleOptions"/>.</summary>
internal sealed class CronnerScheduleOptions : ICronnerScheduleOptions
{
    private readonly List<ICronnerTaskHook> _hooks = [];

    public string? CronString { get; private set; }

    public string? Id { get; private set; }

    public CronnerTaskPriority Priority { get; private set; } = CronnerTaskPriority.Normal;

    public CronnerConcurrencyMode Concurrency { get; private set; } = CronnerConcurrencyMode.DropAndForget;

    public string? Description { get; private set; }

    public IReadOnlyList<ICronnerTaskHook> Hooks => _hooks;

    public ICronnerScheduleOptions WithCron(string cronString)
    {
        CronString = cronString;
        return this;
    }

    public ICronnerScheduleOptions WithId(string id)
    {
        Id = id;
        return this;
    }

    public ICronnerScheduleOptions WithPrio(CronnerTaskPriority priority)
    {
        Priority = priority;
        return this;
    }

    public ICronnerScheduleOptions WithConcurrency(CronnerConcurrencyMode mode)
    {
        Concurrency = mode;
        return this;
    }

    public ICronnerScheduleOptions WithDescription(string description)
    {
        Description = description;
        return this;
    }

    public ICronnerScheduleOptions WithHook(ICronnerTaskHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _hooks.Add(hook);
        return this;
    }

    public ICronnerScheduleOptions WithHook<THook>() where THook : class, ICronnerTaskHook =>
        Add(CronnerHookFactory.FromType(typeof(THook)));

    public ICronnerScheduleOptions OnStart(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Start, handler);
    public ICronnerScheduleOptions OnStart<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Start, typeof(THook), call);
    public ICronnerScheduleOptions OnStart<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Start, typeof(THook), call);

    public ICronnerScheduleOptions OnSuccess(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Success, handler);
    public ICronnerScheduleOptions OnSuccess<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Success, typeof(THook), call);
    public ICronnerScheduleOptions OnSuccess<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Success, typeof(THook), call);

    public ICronnerScheduleOptions OnFail(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Fail, handler);
    public ICronnerScheduleOptions OnFail<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Fail, typeof(THook), call);
    public ICronnerScheduleOptions OnFail<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Fail, typeof(THook), call);

    public ICronnerScheduleOptions OnCancel(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.Cancel, handler);
    public ICronnerScheduleOptions OnCancel<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.Cancel, typeof(THook), call);
    public ICronnerScheduleOptions OnCancel<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.Cancel, typeof(THook), call);

    public ICronnerScheduleOptions OnLockAcquire(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.LockAcquire, handler);
    public ICronnerScheduleOptions OnLockAcquire<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.LockAcquire, typeof(THook), call);
    public ICronnerScheduleOptions OnLockAcquire<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.LockAcquire, typeof(THook), call);

    public ICronnerScheduleOptions OnKeepAlive(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.KeepAlive, handler);
    public ICronnerScheduleOptions OnKeepAlive<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.KeepAlive, typeof(THook), call);
    public ICronnerScheduleOptions OnKeepAlive<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.KeepAlive, typeof(THook), call);

    public ICronnerScheduleOptions OnLockRelease(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.LockRelease, handler);
    public ICronnerScheduleOptions OnLockRelease<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.LockRelease, typeof(THook), call);
    public ICronnerScheduleOptions OnLockRelease<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.LockRelease, typeof(THook), call);

    public ICronnerScheduleOptions OnLockLost(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.LockLost, handler);
    public ICronnerScheduleOptions OnLockLost<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.LockLost, typeof(THook), call);
    public ICronnerScheduleOptions OnLockLost<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.LockLost, typeof(THook), call);

    public ICronnerScheduleOptions OnTotalProgressChange(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.TotalProgressChange, handler);
    public ICronnerScheduleOptions OnTotalProgressChange<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.TotalProgressChange, typeof(THook), call);
    public ICronnerScheduleOptions OnTotalProgressChange<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.TotalProgressChange, typeof(THook), call);

    public ICronnerScheduleOptions OnProgressScopeOpened(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.ProgressScopeOpened, handler);
    public ICronnerScheduleOptions OnProgressScopeOpened<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.ProgressScopeOpened, typeof(THook), call);
    public ICronnerScheduleOptions OnProgressScopeOpened<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.ProgressScopeOpened, typeof(THook), call);

    public ICronnerScheduleOptions OnScopeProgress(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.ScopeProgress, handler);
    public ICronnerScheduleOptions OnScopeProgress<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.ScopeProgress, typeof(THook), call);
    public ICronnerScheduleOptions OnScopeProgress<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.ScopeProgress, typeof(THook), call);

    public ICronnerScheduleOptions OnProgressScopeClosed(Func<CronnerTaskContext, Task> handler) => Delegate(CronnerHookEvent.ProgressScopeClosed, handler);
    public ICronnerScheduleOptions OnProgressScopeClosed<THook>(Expression<Action<THook>> call) => Expr(CronnerHookEvent.ProgressScopeClosed, typeof(THook), call);
    public ICronnerScheduleOptions OnProgressScopeClosed<THook>(Expression<Func<THook, Task>> call) => Expr(CronnerHookEvent.ProgressScopeClosed, typeof(THook), call);

    private ICronnerScheduleOptions Delegate(CronnerHookEvent hookEvent, Func<CronnerTaskContext, Task> handler) =>
        Add(CronnerHookFactory.FromDelegate(hookEvent, handler));

    private ICronnerScheduleOptions Expr(CronnerHookEvent hookEvent, Type targetType, LambdaExpression call) =>
        Add(CronnerHookFactory.FromExpression(hookEvent, targetType, call));

    private ICronnerScheduleOptions Add(ICronnerTaskHook hook)
    {
        _hooks.Add(hook);
        return this;
    }
}
