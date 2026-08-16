using DotnetCronner.Analyzers;
using Shouldly;

namespace DotnetCronner.Tests;

public class CronSyntaxTests
{
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 9-11,17 * * *")]
    [InlineData("0 0 * * MON")]
    [InlineData("0 0 * * 7")]
    [InlineData("*/10 * * * * *")] // 6-field with seconds
    [InlineData("0 0 1 JAN *")]
    [InlineData("0 0 15 * MON-FRI")]
    public void ValidExpressions_ReturnNull(string expression)
    {
        CronSyntax.Validate(expression).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * *")]           // too few fields
    [InlineData("* * * * * * *")]   // too many fields
    [InlineData("60 * * * *")]      // minute out of range
    [InlineData("* 24 * * *")]      // hour out of range
    [InlineData("* * * * 8")]       // day-of-week out of range
    [InlineData("*/0 * * * *")]     // zero step
    [InlineData("bad * * * *")]     // non-numeric token
    [InlineData("0 0 L * *")]       // unsupported token
    [InlineData("11-9 * * * *")]    // reversed range
    public void InvalidExpressions_ReturnReason(string expression)
    {
        CronSyntax.Validate(expression).ShouldNotBeNull();
    }
}
