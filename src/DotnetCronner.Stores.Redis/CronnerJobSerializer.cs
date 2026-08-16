using System.Text.Json;

namespace DotnetCronner;

/// <summary>Shared JSON serialization for persisting <see cref="CronnerJob"/> instances in Redis.</summary>
internal static class CronnerJobSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(CronnerJob job) => JsonSerializer.Serialize(job, Options);

    public static CronnerJob? Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<CronnerJob>(json, Options);
}
