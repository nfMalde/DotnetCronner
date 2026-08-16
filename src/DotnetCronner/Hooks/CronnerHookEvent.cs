namespace DotnetCronner;

/// <summary>The lifecycle and lock-lifecycle events a hook can fire on.</summary>
internal enum CronnerHookEvent
{
    /// <summary>Before the task method runs.</summary>
    Start,

    /// <summary>After a successful run.</summary>
    Success,

    /// <summary>After the task throws.</summary>
    Fail,

    /// <summary>After the task is cancelled.</summary>
    Cancel,

    /// <summary>When the execution lock is claimed.</summary>
    LockAcquire,

    /// <summary>Each time the execution lock is renewed (keepalive).</summary>
    KeepAlive,

    /// <summary>When the execution lock is released.</summary>
    LockRelease,

    /// <summary>When the execution lock is lost mid-run.</summary>
    LockLost,

    /// <summary>When total progress is reported.</summary>
    TotalProgressChange,

    /// <summary>When a progress scope is opened.</summary>
    ProgressScopeOpened,

    /// <summary>When progress is reported to a scope.</summary>
    ScopeProgress,

    /// <summary>When a progress scope is closed.</summary>
    ProgressScopeClosed,
}

/// <summary>Routes a <see cref="CronnerHookEvent"/> to the matching <see cref="ICronnerTaskHook"/> method.</summary>
internal static class CronnerHookInvoke
{
    public static Task Dispatch(ICronnerTaskHook hook, CronnerHookEvent hookEvent, CronnerTaskContext context) => hookEvent switch
    {
        CronnerHookEvent.Start => hook.OnStartAsync(context),
        CronnerHookEvent.Success => hook.OnSuccessAsync(context),
        CronnerHookEvent.Fail => hook.OnFailAsync(context),
        CronnerHookEvent.Cancel => hook.OnCancelAsync(context),
        CronnerHookEvent.LockAcquire => hook.OnLockAcquireAsync(context),
        CronnerHookEvent.KeepAlive => hook.OnKeepAliveAsync(context),
        CronnerHookEvent.LockRelease => hook.OnLockReleaseAsync(context),
        CronnerHookEvent.LockLost => hook.OnLockLostAsync(context),
        CronnerHookEvent.TotalProgressChange => hook.OnTotalProgressChangeAsync(context),
        CronnerHookEvent.ProgressScopeOpened => hook.OnProgressScopeOpenedAsync(context),
        CronnerHookEvent.ScopeProgress => hook.OnScopeProgressAsync(context),
        CronnerHookEvent.ProgressScopeClosed => hook.OnProgressScopeClosedAsync(context),
        _ => Task.CompletedTask,
    };
}
