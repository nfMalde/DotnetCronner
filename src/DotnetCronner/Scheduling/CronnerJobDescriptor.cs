using System.Reflection;

namespace DotnetCronner;

/// <summary>
/// The in-memory description of how to run a task: which method to invoke and how to supply its
/// arguments. Descriptors are held in the <see cref="CronnerRegistry"/> and looked up by id; the
/// persisted <see cref="CronnerJob"/> only carries schedule and state.
/// </summary>
public sealed class CronnerJobDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    public CronnerJobDescriptor(
        string id,
        string name,
        string? cronString,
        Type targetType,
        MethodInfo method,
        IReadOnlyList<CronnerArgument> arguments,
        CronnerTaskPriority priority = CronnerTaskPriority.Normal,
        CronnerConcurrencyMode concurrency = CronnerConcurrencyMode.DropAndForget,
        IReadOnlyList<ICronnerTaskHook>? hooks = null)
    {
        Id = id;
        Name = name;
        CronString = cronString;
        TargetType = targetType;
        Method = method;
        Arguments = arguments;
        Priority = priority;
        Concurrency = concurrency;
        Hooks = hooks ?? [];
    }

    /// <summary>Stable unique id of the task.</summary>
    public string Id { get; }

    /// <summary>Fully qualified <c>Type.Method</c> name.</summary>
    public string Name { get; }

    /// <summary>The cron expression, or <c>null</c> for a manual / one-shot task.</summary>
    public string? CronString { get; }

    /// <summary>The type that declares <see cref="Method"/>.</summary>
    public Type TargetType { get; }

    /// <summary>The method to invoke.</summary>
    public MethodInfo Method { get; }

    /// <summary>The plan for supplying each method argument.</summary>
    public IReadOnlyList<CronnerArgument> Arguments { get; }

    /// <summary>The task's execution priority.</summary>
    public CronnerTaskPriority Priority { get; }

    /// <summary>What happens when the task becomes due while a previous run is still executing.</summary>
    public CronnerConcurrencyMode Concurrency { get; }

    /// <summary>Hooks attached to this specific schedule (in addition to any global hooks).</summary>
    public IReadOnlyList<ICronnerTaskHook> Hooks { get; }
}
