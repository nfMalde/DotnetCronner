namespace DotnetCronner.Tests.Shared;

// The unit project needs nothing external: the shared contract tests always run here.
public static partial class ContractTestEnvironment
{
    private static partial string? Probe() => null;
}
