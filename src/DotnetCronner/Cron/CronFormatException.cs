namespace DotnetCronner;

/// <summary>
/// Thrown when a cron expression cannot be parsed.
/// </summary>
public sealed class CronFormatException : FormatException
{
    /// <summary>Creates the exception with the given message.</summary>
    public CronFormatException(string message) : base(message)
    {
    }
}
