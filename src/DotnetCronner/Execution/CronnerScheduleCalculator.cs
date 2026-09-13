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

    /// <summary>
    /// Counts the occurrences of <paramref name="cronString"/> in the interval (<paramref name="from"/>,
    /// <paramref name="to"/>] (strictly after <paramref name="from"/>, up to and including <paramref name="to"/>).
    /// When that count exceeds <paramref name="keepNewest"/>, <c>ClampFrom</c> is the occurrence from which
    /// exactly <paramref name="keepNewest"/> remain — so a bounded catch-up (FireAll) can start there and drop
    /// the older ones. Uses O(<paramref name="keepNewest"/>) memory regardless of how many were missed.
    /// </summary>
    public (int Count, DateTimeOffset? ClampFrom) CountMissed(
        string? cronString, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo timeZone, int keepNewest)
    {
        if (string.IsNullOrWhiteSpace(cronString) || to <= from)
            return (0, null);

        var expression = _cache.GetOrAdd(cronString, CronExpression.Parse);
        var window = new Queue<DateTimeOffset>(Math.Max(1, keepNewest) + 1);
        var count = 0;
        var cursor = from;
        const int safety = 5_000_000; // absurd-downtime guard so enumeration can never run away

        while (count < safety)
        {
            var next = expression.GetNextOccurrence(cursor, timeZone);
            if (next is null || next.Value > to)
                break;

            count++;
            if (keepNewest > 0)
            {
                window.Enqueue(next.Value);
                if (window.Count > keepNewest)
                    window.Dequeue();
            }

            cursor = next.Value;
        }

        var clampFrom = keepNewest > 0 && count > keepNewest ? window.Peek() : (DateTimeOffset?)null;
        return (count, clampFrom);
    }
}
