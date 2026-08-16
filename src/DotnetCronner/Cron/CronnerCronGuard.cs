namespace DotnetCronner;

/// <summary>Validates cron expressions at registration time so a bad expression fails fast, naming the task.</summary>
internal static class CronnerCronGuard
{
    public static void Validate(string taskId, string? cronString)
    {
        if (string.IsNullOrWhiteSpace(cronString))
            return;

        try
        {
            _ = CronExpression.Parse(cronString!);
        }
        catch (CronFormatException ex)
        {
            throw new CronFormatException(
                $"DotnetCronner task '{taskId}' has an invalid cron expression \"{cronString}\": {ex.Message}");
        }
    }
}
