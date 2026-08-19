namespace DotnetCronner.Tests.Shared;

/// <summary>
/// A <see cref="FactAttribute"/> for the shared contract tests: it is skipped when the project that compiles the
/// shared sources says its environment is not available (the integration project: no Docker).
/// </summary>
public sealed class ContractFactAttribute : FactAttribute
{
    public ContractFactAttribute() => Skip = ContractTestEnvironment.SkipReason;
}

/// <summary>
/// Each project that links the shared tests supplies the <see cref="Probe"/> half: the unit project never skips, the
/// integration project skips when Docker cannot be reached (unless <c>CRONNER_REQUIRE_DOCKER=1</c> demands it).
/// </summary>
public static partial class ContractTestEnvironment
{
    /// <summary><c>null</c> when the contract tests can run; otherwise the reason they are skipped.</summary>
    public static string? SkipReason { get; } = Probe();

    private static partial string? Probe();
}
