using DotNet.Testcontainers.Configurations;

namespace DotnetCronner.Tests.Shared;

/// <summary>
/// Decides once per test run whether Docker is reachable. Without Docker the container-backed tests are SKIPPED;
/// with <c>CRONNER_REQUIRE_DOCKER=1</c> (set in CI) they are never skipped, so a missing/broken Docker fails the
/// build loudly instead of quietly passing with everything skipped.
/// </summary>
public static class DockerProbe
{
    public const string RequireVariable = "CRONNER_REQUIRE_DOCKER";

    public static bool IsRequired { get; } = Environment.GetEnvironmentVariable(RequireVariable) == "1";

    public static bool IsAvailable { get; } = IsRequired || Detect();

    private static bool Detect()
    {
        try
        {
            // Testcontainers resolves the Docker endpoint (DOCKER_HOST, the desktop socket, ~/.testcontainers.properties, …)
            // lazily; a null auth config means it found nothing to talk to. A configured-but-dead endpoint (a stale
            // DOCKER_HOST, Docker Desktop not running) is caught by an actual ping with a short timeout.
            var auth = TestcontainersSettings.OS.DockerEndpointAuthConfig;
            if (auth is null)
                return false;

            using var client = auth.GetDockerClientBuilder(Guid.NewGuid()).Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            client.System.PingAsync(cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// The integration project's half of the shared ContractTestEnvironment: skip the linked contract tests when Docker
// is not there (unless CI demands it).
public static partial class ContractTestEnvironment
{
    private static partial string? Probe() =>
        DockerProbe.IsAvailable
            ? null
            : $"Docker is not available; the container-backed contract tests are skipped (set {DockerProbe.RequireVariable}=1 to fail instead).";
}
