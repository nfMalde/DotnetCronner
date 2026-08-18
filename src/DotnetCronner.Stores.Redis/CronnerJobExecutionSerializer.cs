using System.Text.Json;

namespace DotnetCronner;

/// <summary>Shared JSON serialization for persisting <see cref="CronnerJobExecution"/> records in Redis.</summary>
internal static class CronnerJobExecutionSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(CronnerJobExecution execution) => JsonSerializer.Serialize(execution, Options);

    public static CronnerJobExecution? Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<CronnerJobExecution>(json, Options);
}
