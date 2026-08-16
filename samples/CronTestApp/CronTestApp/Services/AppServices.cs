namespace CronTestApp.Services;

/// <summary>A scoped dependency jobs take through DI, to prove parameter resolution works.</summary>
public interface IGreeter
{
    /// <summary>Greets <paramref name="who"/> and records it in the activity log.</summary>
    void Greet(string who, string jobId);
}

/// <inheritdoc />
public sealed class ConsoleGreeter(ILogger<ConsoleGreeter> logger, JobActivityLog activity, ScopeMarker scope) : IGreeter
{
    /// <inheritdoc />
    public void Greet(string who, string jobId)
    {
        logger.LogInformation("Hello {Who} from {JobId} (scope {Scope})", who, jobId, scope.Id);
        activity.Record(jobId, $"greeted '{who}' via IGreeter (scope {scope.Id})");
    }
}

/// <summary>
/// A scoped marker whose id changes with every DI scope — jobs log it so you can see that each run
/// really does get its own scope.
/// </summary>
public sealed class ScopeMarker
{
    /// <summary>The id of this scope.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
}

/// <summary>A value that is only available from a scoped accessor — the case the factory overload of
/// <c>HasParam</c> exists for.</summary>
/// <param name="Name">The tenant name.</param>
public sealed record Tenant(string Name);

/// <summary>Supplies the tenant for the current scope.</summary>
public interface ITenantAccessor
{
    /// <summary>The tenant of the current scope.</summary>
    Tenant Current { get; }
}

/// <inheritdoc />
public sealed class RoundRobinTenantAccessor : ITenantAccessor
{
    private static int _counter;

    private readonly Lazy<Tenant> _current = new(() =>
        new Tenant(Interlocked.Increment(ref _counter) % 2 == 0 ? "contoso" : "acme"));

    /// <inheritdoc />
    public Tenant Current => _current.Value;
}
