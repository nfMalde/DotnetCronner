namespace DotnetCronner;

/// <summary>
/// The state of a single job execution recorded in the execution history. A run is
/// <see cref="Running"/> while in flight and finalizes to one of the terminal values.
/// </summary>
public enum JobExecutionStatus
{
    /// <summary>The run is currently executing and has not finished yet (a run left in this state is a crash/stall).</summary>
    Running = 0,

    /// <summary>The run completed successfully.</summary>
    Succeeded = 1,

    /// <summary>The run threw an exception.</summary>
    Failed = 2,

    /// <summary>The run was cancelled (via the client or host shutdown).</summary>
    Cancelled = 3,
}
