using System.Globalization;

namespace DotnetCronner;

/// <summary>
/// A zero-dependency cron expression parser and occurrence calculator.
/// </summary>
/// <remarks>
/// Supported format is a 5-field expression <c>minute hour day-of-month month day-of-week</c>, or a
/// 6-field expression where the first field is <c>seconds</c>. Each field supports <c>*</c>, lists
/// (<c>,</c>), ranges (<c>-</c>), steps (<c>/</c>), numeric values, and 3-letter names for months
/// (<c>JAN</c>..<c>DEC</c>) and days (<c>SUN</c>..<c>SAT</c>). <c>?</c> is accepted as an alias for
/// <c>*</c> in the day-of-month and day-of-week fields. Day-of-week accepts both <c>0</c> and <c>7</c>
/// for Sunday.
/// <para>
/// When both day-of-month and day-of-week are restricted, an occurrence matches if <em>either</em>
/// field matches (Vixie cron semantics). The extended tokens <c>L</c>, <c>W</c> and <c>#</c> are not
/// supported in this version and raise a <see cref="CronFormatException"/>.
/// </para>
/// </remarks>
public sealed class CronExpression
{
    private const int MaxSearchYears = 5;

    private readonly ulong _seconds;      // bits 0..59
    private readonly ulong _minutes;      // bits 0..59
    private readonly uint _hours;         // bits 0..23
    private readonly uint _daysOfMonth;   // bits 1..31
    private readonly int _months;         // bits 1..12
    private readonly int _daysOfWeek;     // bits 0..6 (Sunday = 0)
    private readonly bool _hasSeconds;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;
    private readonly string _expression;

    private CronExpression(
        ulong seconds, ulong minutes, uint hours, uint daysOfMonth, int months, int daysOfWeek,
        bool hasSeconds, bool domRestricted, bool dowRestricted, string expression)
    {
        _seconds = seconds;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _hasSeconds = hasSeconds;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
        _expression = expression;
    }

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    /// <summary>Parses a cron <paramref name="expression"/>.</summary>
    /// <exception cref="CronFormatException">The expression is malformed or uses unsupported syntax.</exception>
    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new CronFormatException("Cron expression must not be empty.");

        var raw = expression.Trim();
        var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (5 or 6))
            throw new CronFormatException(
                $"Cron expression '{expression}' must have 5 fields (minute hour day-of-month month day-of-week) " +
                "or 6 fields with a leading seconds field.");

        var hasSeconds = fields.Length == 6;
        var i = 0;
        var seconds = hasSeconds ? ParseField(fields[i++], 0, 59, null, out _) : 1UL << 0;
        var minutes = ParseField(fields[i++], 0, 59, null, out _);
        var hours = (uint)ParseField(fields[i++], 0, 23, null, out _);
        var daysOfMonth = (uint)ParseField(fields[i++], 1, 31, null, out var domRestricted);
        var months = (int)ParseField(fields[i++], 1, 12, MonthNames, out _);

        // Day-of-week allows both 0 and 7 for Sunday; parse over 0..7 then fold bit 7 onto bit 0.
        var daysOfWeek = (int)ParseField(fields[i], 0, 7, DayNames, out var dowRestricted);
        if ((daysOfWeek & (1 << 7)) != 0)
            daysOfWeek = (daysOfWeek & ~(1 << 7)) | (1 << 0);

        return new CronExpression(
            seconds, minutes, hours, daysOfMonth, months, daysOfWeek,
            hasSeconds, domRestricted, dowRestricted, raw);
    }

    /// <summary>Returns the next occurrence strictly after <paramref name="from"/> in the given time zone, or <c>null</c> if none exists within the search horizon.</summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var unit = _hasSeconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        var local = TimeZoneInfo.ConvertTime(from, timeZone).DateTime; // wall-clock time, Kind = Unspecified
        var t = Truncate(local, unit) + unit; // strictly after 'from'
        var limit = t.AddYears(MaxSearchYears);

        while (t < limit)
        {
            if ((_months & (1 << t.Month)) == 0)
            {
                t = new DateTime(t.Year, t.Month, 1).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = new DateTime(t.Year, t.Month, t.Day).AddDays(1);
                continue;
            }

            if ((_hours & (1u << t.Hour)) == 0)
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1);
                continue;
            }

            if ((_minutes & (1UL << t.Minute)) == 0)
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0).AddMinutes(1);
                continue;
            }

            if (_hasSeconds && (_seconds & (1UL << t.Second)) == 0)
            {
                t = t.AddSeconds(1);
                continue;
            }

            // All fields match. Skip wall-clock times that do not exist (spring-forward DST gap).
            if (timeZone.IsInvalidTime(t))
            {
                t += unit;
                continue;
            }

            var offset = timeZone.GetUtcOffset(t);
            return new DateTimeOffset(t, offset);
        }

        return null;
    }

    /// <summary>The original (trimmed) cron expression.</summary>
    public override string ToString() => _expression;

    private bool DayMatches(DateTime t)
    {
        var domMatch = (_daysOfMonth & (1u << t.Day)) != 0;
        var dowMatch = (_daysOfWeek & (1 << (int)t.DayOfWeek)) != 0;

        if (_domRestricted && _dowRestricted)
            return domMatch || dowMatch; // Vixie cron: either field matches.
        if (_domRestricted)
            return domMatch;
        if (_dowRestricted)
            return dowMatch;
        return true;
    }

    private static ulong ParseField(string field, int min, int max, string[]? names, out bool restricted)
    {
        if (field is "*" or "?")
        {
            restricted = false;
            return FullMask(min, max);
        }

        restricted = true;
        ulong mask = 0;
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
            mask |= ParsePart(part, min, max, names);

        if (mask == 0)
            throw new CronFormatException($"Cron field '{field}' does not resolve to any value.");

        return mask;
    }

    private static ulong ParsePart(string part, int min, int max, string[]? names)
    {
        var step = 1;
        var body = part;

        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            body = part[..slash];
            var stepText = part[(slash + 1)..];
            if (!int.TryParse(stepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
                throw new CronFormatException($"Invalid step '{stepText}' in cron field part '{part}'.");
        }

        int rangeStart, rangeEnd;
        if (body is "*")
        {
            rangeStart = min;
            rangeEnd = max;
        }
        else
        {
            var dash = body.IndexOf('-');
            if (dash > 0)
            {
                rangeStart = ParseValue(body[..dash], min, max, names);
                rangeEnd = ParseValue(body[(dash + 1)..], min, max, names);
            }
            else
            {
                rangeStart = ParseValue(body, min, max, names);
                // "a/step" (single value with a step) means from a through the maximum.
                rangeEnd = slash >= 0 ? max : rangeStart;
            }
        }

        if (rangeStart > rangeEnd)
            throw new CronFormatException($"Cron range start is greater than end in part '{part}'.");

        ulong mask = 0;
        for (var v = rangeStart; v <= rangeEnd; v += step)
            mask |= 1UL << v;
        return mask;
    }

    private static int ParseValue(string token, int min, int max, string[]? names)
    {
        token = token.Trim();
        int value;
        if (names is not null && !int.TryParse(token, out _))
        {
            var index = Array.FindIndex(names, n => n.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new CronFormatException($"Unknown cron token '{token}'.");
            value = index + (min == 1 ? 1 : 0); // months are 1-based, days are 0-based
        }
        else if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            throw new CronFormatException($"'{token}' is not a valid cron value.");
        }

        if (value < min || value > max)
            throw new CronFormatException($"Cron value '{value}' is out of range [{min}, {max}].");

        return value;
    }

    private static ulong FullMask(int min, int max)
    {
        ulong mask = 0;
        for (var v = min; v <= max; v++)
            mask |= 1UL << v;
        return mask;
    }

    private static DateTime Truncate(DateTime value, TimeSpan unit) =>
        new(value.Ticks - (value.Ticks % unit.Ticks), value.Kind);
}
