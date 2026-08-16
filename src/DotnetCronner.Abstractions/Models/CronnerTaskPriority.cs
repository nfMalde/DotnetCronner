namespace DotnetCronner;

/// <summary>
/// Relative execution priority of a task. When several tasks are due at the same time, higher
/// priorities are claimed and dispatched first.
/// </summary>
public enum CronnerTaskPriority
{
    /// <summary>Runs after normal tasks.</summary>
    Low = 0,

    /// <summary>The default priority.</summary>
    Normal = 1,

    /// <summary>Runs before normal tasks.</summary>
    High = 2,

    /// <summary>Runs before all lower priorities.</summary>
    Critical = 3,
}
