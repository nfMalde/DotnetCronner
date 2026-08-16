namespace DotnetCronner;

/// <summary>How a task method parameter is supplied when the task runs.</summary>
public enum CronnerArgumentKind
{
    /// <summary>Resolved from the scoped <see cref="IServiceProvider"/>.</summary>
    Service,

    /// <summary>Bound to the task's <see cref="System.Threading.CancellationToken"/>.</summary>
    CancellationToken,

    /// <summary>A fixed value captured from the scheduling expression.</summary>
    Literal,

    /// <summary>Produced at run time by a caller-supplied factory over the scoped <see cref="IServiceProvider"/>.</summary>
    Factory,
}

/// <summary>Describes a single argument passed to a task method at execution time.</summary>
public sealed class CronnerArgument
{
    private CronnerArgument(CronnerArgumentKind kind, Type type, object? value, Func<IServiceProvider, object?>? factory)
    {
        Kind = kind;
        Type = type;
        Value = value;
        Factory = factory;
    }

    /// <summary>How the argument is supplied.</summary>
    public CronnerArgumentKind Kind { get; }

    /// <summary>The parameter type.</summary>
    public Type Type { get; }

    /// <summary>The captured literal value (only meaningful when <see cref="Kind"/> is <see cref="CronnerArgumentKind.Literal"/>).</summary>
    public object? Value { get; }

    /// <summary>The run-time factory (only meaningful when <see cref="Kind"/> is <see cref="CronnerArgumentKind.Factory"/>).</summary>
    public Func<IServiceProvider, object?>? Factory { get; }

    /// <summary>Creates a service-injected argument of the given type.</summary>
    public static CronnerArgument Service(Type type) => new(CronnerArgumentKind.Service, type, null, null);

    /// <summary>Creates a cancellation-token argument.</summary>
    public static CronnerArgument CancellationToken() => new(CronnerArgumentKind.CancellationToken, typeof(CancellationToken), null, null);

    /// <summary>Creates a fixed literal argument.</summary>
    public static CronnerArgument Literal(Type type, object? value) => new(CronnerArgumentKind.Literal, type, value, null);

    /// <summary>Creates an argument produced at run time by <paramref name="factory"/>.</summary>
    public static CronnerArgument FromFactory(Type type, Func<IServiceProvider, object?> factory) =>
        new(CronnerArgumentKind.Factory, type, null, factory);
}
