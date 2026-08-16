using DotnetCronner;
using Shouldly;

namespace DotnetCronner.Tests;

public class CronExpressionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset Utc0(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);

    [Fact]
    public void EveryMinute_ReturnsNextMinute()
    {
        var next = CronExpression.Parse("* * * * *").GetNextOccurrence(Utc0(2026, 1, 1, 10, 30, 15), Utc);
        next.ShouldBe(Utc0(2026, 1, 1, 10, 31));
    }

    [Fact]
    public void SpecificTime_DailyRun()
    {
        var next = CronExpression.Parse("30 9 * * *").GetNextOccurrence(Utc0(2026, 1, 1, 10, 0), Utc);
        next.ShouldBe(Utc0(2026, 1, 2, 9, 30));
    }

    [Fact]
    public void StepMinutes_EveryFifteen()
    {
        var next = CronExpression.Parse("*/15 * * * *").GetNextOccurrence(Utc0(2026, 1, 1, 10, 5), Utc);
        next.ShouldBe(Utc0(2026, 1, 1, 10, 15));
    }

    [Fact]
    public void SixFields_SecondsSupported()
    {
        var next = CronExpression.Parse("*/10 * * * * *").GetNextOccurrence(Utc0(2026, 1, 1, 10, 0, 3), Utc);
        next.ShouldBe(Utc0(2026, 1, 1, 10, 0, 10));
    }

    [Fact]
    public void DayOfWeek_ByName()
    {
        // Mondays at 00:00. 2026-01-01 is a Thursday, so the next Monday is 2026-01-05.
        var next = CronExpression.Parse("0 0 * * MON").GetNextOccurrence(Utc0(2026, 1, 1, 12, 0), Utc);
        next.ShouldBe(Utc0(2026, 1, 5, 0, 0));
    }

    [Fact]
    public void DayOfWeek_SundayAsSeven()
    {
        var seven = CronExpression.Parse("0 0 * * 7").GetNextOccurrence(Utc0(2026, 1, 1, 12, 0), Utc);
        var zero = CronExpression.Parse("0 0 * * 0").GetNextOccurrence(Utc0(2026, 1, 1, 12, 0), Utc);
        seven.ShouldBe(zero);
        seven.ShouldBe(Utc0(2026, 1, 4, 0, 0)); // first Sunday after Jan 1 2026
    }

    [Fact]
    public void Range_And_List()
    {
        var next = CronExpression.Parse("0 9-11,17 * * *").GetNextOccurrence(Utc0(2026, 1, 1, 11, 30), Utc);
        next.ShouldBe(Utc0(2026, 1, 1, 17, 0));
    }

    [Fact]
    public void MonthName_Boundary()
    {
        var next = CronExpression.Parse("0 0 1 JAN *").GetNextOccurrence(Utc0(2026, 6, 1, 0, 0), Utc);
        next.ShouldBe(Utc0(2027, 1, 1, 0, 0));
    }

    [Fact]
    public void BothDomAndDow_MatchEither()
    {
        // Vixie semantics: runs on the 15th OR on any Monday; the first Monday comes before the 15th.
        var next = CronExpression.Parse("0 0 15 * MON").GetNextOccurrence(Utc0(2026, 1, 1, 0, 0), Utc);
        next.ShouldBe(Utc0(2026, 1, 5, 0, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * *")]
    [InlineData("60 * * * *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("bad * * * *")]
    [InlineData("0 0 L * *")]
    public void InvalidExpressions_Throw(string expression)
    {
        Should.Throw<CronFormatException>(() => CronExpression.Parse(expression));
    }

    [Fact]
    public void Impossible_ReturnsNull()
    {
        // Feb 30 never occurs.
        CronExpression.Parse("0 0 30 2 *").GetNextOccurrence(Utc0(2026, 1, 1, 0, 0), Utc).ShouldBeNull();
    }
}
