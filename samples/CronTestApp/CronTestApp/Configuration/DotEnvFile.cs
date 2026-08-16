namespace CronTestApp.Configuration;

/// <summary>
/// A minimal <c>.env</c> reader so the same file steers the app whether it runs under Docker Compose
/// (which reads <c>.env</c> itself) or straight from Visual Studio / <c>dotnet run</c> (which does not).
/// </summary>
/// <remarks>
/// Existing environment variables always win, so values set by <c>docker compose</c> —
/// the in-container host names, for example — are never overwritten by the file.
/// </remarks>
public static class DotEnvFile
{
    /// <summary>
    /// Loads the nearest <c>.env</c>, searching the current directory and up to <paramref name="maxDepth"/>
    /// parent directories. Returns the file that was loaded, or <c>null</c> when there is none.
    /// </summary>
    public static string? Load(string? startDirectory = null, int maxDepth = 4)
    {
        var directory = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());

        for (var depth = 0; directory is not null && depth <= maxDepth; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(candidate))
                continue;

            foreach (var line in File.ReadAllLines(candidate))
                Apply(line);

            return candidate;
        }

        return null;
    }

    private static void Apply(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            return;

        if (text.StartsWith("export ", StringComparison.Ordinal))
            text = text["export ".Length..].TrimStart();

        var separator = text.IndexOf('=');
        if (separator <= 0)
            return;

        var key = text[..separator].Trim();
        var value = text[(separator + 1)..].Trim();

        // Strip one layer of matching quotes, the way docker compose does.
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];

        // Never override what the process was actually started with (docker compose, launchSettings, CI).
        if (Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, value);
    }
}
