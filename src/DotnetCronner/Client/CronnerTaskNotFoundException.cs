namespace DotnetCronner;

/// <summary>Thrown when an operation references a task id that is not known to the store.</summary>
public sealed class CronnerTaskNotFoundException : InvalidOperationException
{
    /// <summary>Creates the exception for the given missing id.</summary>
    public CronnerTaskNotFoundException(string id)
        : base($"No DotnetCronner task with id '{id}' was found.")
    {
        Id = id;
    }

    /// <summary>The id that was not found.</summary>
    public string Id { get; }
}
