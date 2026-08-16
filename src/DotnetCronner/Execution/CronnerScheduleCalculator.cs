using System.Collections.Concurrent;

namespace DotnetCronner;

/// <summary>Computes next run times from cron strings, caching parsed expressions. Registered as a singleton.</summary>
public sealed class CronnerScheduleCalculator
{
    private readonly ConcurrentDictionary<string, CronExpression> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the next occurrence of <paramref name="cronString"/> strictly after <paramref name="from"/>,
    /// or <c>null</c> when the string is empty (a manual / one-shot task) or has no future occurrence.
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(string? cronString, DateTimeOffset from, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(cronString))
            return null;

        var expression = _cache.GetOrAdd(cronString, CronExpression.Parse);
        return expression.GetNextOccurrence(from, timeZone);
    }
}
