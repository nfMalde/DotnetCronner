namespace DotnetCronner;

/// <summary>
/// Thrown when a task is registered with an id that is already in use.
/// </summary>
public sealed class DuplicateCronnerTaskException : InvalidOperationException
{
    /// <summary>Creates the exception for the given duplicate id.</summary>
    public DuplicateCronnerTaskException(string id)
        : base($"A DotnetCronner task with id '{id}' has already been registered. Ids must be unique.")
    {
        Id = id;
    }

    /// <summary>The id that collided.</summary>
    public string Id { get; }
}
