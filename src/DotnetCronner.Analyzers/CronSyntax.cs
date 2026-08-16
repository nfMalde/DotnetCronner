using System;

namespace DotnetCronner.Analyzers;

/// <summary>
/// A dependency-free cron validator that mirrors the runtime parser's rules closely enough to catch
/// mistakes at build time. Returns <c>null</c> when the expression is valid, otherwise a human-readable
/// reason. It is intentionally conservative: anything it is unsure about is treated as valid and left to
/// the (authoritative) runtime parser.
/// </summary>
public static class CronSyntax
{
    private static readonly string[] MonthNames =
        { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    private static readonly string[] DayNames =
        { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    /// <summary>Validates a cron expression. Returns <c>null</c> if valid, otherwise the reason it is invalid.</summary>
    public static string? Validate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "Cron expression must not be empty.";

        var fields = expression!.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 && fields.Length != 6)
            return $"Cron expression must have 5 fields, or 6 with a leading seconds field, but found {fields.Length}.";

        var hasSeconds = fields.Length == 6;
        var index = 0;

        if (hasSeconds && ValidateField(fields[index++], 0, 59, null) is { } secondsError)
            return secondsError;
        if (ValidateField(fields[index++], 0, 59, null) is { } minuteError)
            return minuteError;
        if (ValidateField(fields[index++], 0, 23, null) is { } hourError)
            return hourError;
        if (ValidateField(fields[index++], 1, 31, null) is { } domError)
            return domError;
        if (ValidateField(fields[index++], 1, 12, MonthNames) is { } monthError)
            return monthError;
        if (ValidateField(fields[index], 0, 7, DayNames) is { } dowError)
            return dowError;

        return null;
    }

    private static string? ValidateField(string field, int min, int max, string[]? names)
    {
        if (field == "*" || field == "?")
            return null;

        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
                return $"Cron field '{field}' has an empty entry.";
            if (ValidatePart(part, min, max, names) is { } error)
                return error;
        }

        return null;
    }

    private static string? ValidatePart(string part, int min, int max, string[]? names)
    {
        var body = part;
        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            var stepText = part.Substring(slash + 1);
            body = part.Substring(0, slash);
            if (!int.TryParse(stepText, out var step) || step <= 0)
                return $"Invalid step '{stepText}' in cron part '{part}'.";
        }

        if (body == "*")
            return null;

        var dash = body.IndexOf('-');
        if (dash > 0)
        {
            if (TryValue(body.Substring(0, dash), min, max, names, out var start) is { } startError)
                return startError;
            if (TryValue(body.Substring(dash + 1), min, max, names, out var end) is { } endError)
                return endError;
            if (start > end)
                return $"Cron range start is greater than end in '{part}'.";
            return null;
        }

        return TryValue(body, min, max, names, out _);
    }

    private static string? TryValue(string token, int min, int max, string[]? names, out int value)
    {
        value = -1;
        token = token.Trim();

        if (int.TryParse(token, out value))
            return value < min || value > max ? $"Cron value '{value}' is out of range [{min}, {max}]." : null;

        if (names != null)
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i].Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    value = min == 1 ? i + 1 : i; // months are 1-based, days are 0-based
                    return null;
                }
            }

            return $"Unknown cron token '{token}'.";
        }

        return $"'{token}' is not a valid cron value.";
    }
}
