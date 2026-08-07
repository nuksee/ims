using FluentAssertions;
using Ims.Core.Data;
using Xunit;

namespace Ims.Core.Tests;

public class DateTimeQualifierTests
{
    [Fact]
    public void Rejects_an_inverted_range()
    {
        // Informix qualifiers run from a larger field to a smaller one.
        Action act = () => _ = new DateTimeQualifier(DateTimeField.Second, DateTimeField.Year);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(DateTimeField.Year, DateTimeField.Second, "YEAR TO SECOND")]
    [InlineData(DateTimeField.Year, DateTimeField.Day, "YEAR TO DAY")]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction3, "YEAR TO FRACTION(3)")]
    [InlineData(DateTimeField.Hour, DateTimeField.Minute, "HOUR TO MINUTE")]
    [InlineData(DateTimeField.Year, DateTimeField.Year, "YEAR")]
    public void Writes_the_qualifier_the_way_Informix_does(
        DateTimeField start,
        DateTimeField end,
        string expected)
    {
        new DateTimeQualifier(start, end).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(DateTimeField.Year, DateTimeField.Second, 0)]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction1, 1)]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction3, 3)]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction5, 5)]
    public void Reports_its_fractional_precision(DateTimeField start, DateTimeField end, int expected)
    {
        new DateTimeQualifier(start, end).FractionalDigits.Should().Be(expected);
    }

    [Fact]
    public void A_date_only_qualifier_renders_no_time_of_day()
    {
        // PR-4.5: a DATETIME YEAR TO DAY column must not display a spurious 00:00:00,
        // which is what happens when the qualifier is dropped and the value is
        // formatted as a plain DateTime.
        var value = new QualifiedDateTime(
            new DateTime(2026, 8, 5, 14, 37, 12, DateTimeKind.Unspecified),
            DateTimeQualifier.YearToDay);

        value.ToString().Should().Be("2026-08-05");
    }

    [Fact]
    public void A_fractional_qualifier_renders_to_its_declared_precision()
    {
        var value = new QualifiedDateTime(
            new DateTime(2026, 8, 5, 14, 37, 12, 456, DateTimeKind.Unspecified),
            DateTimeQualifier.YearToFraction3);

        value.ToString().Should().Be("2026-08-05 14:37:12.456");
    }

    [Fact]
    public void Year_to_second_renders_without_a_fraction()
    {
        var value = new QualifiedDateTime(
            new DateTime(2026, 8, 5, 14, 37, 12, 456, DateTimeKind.Unspecified),
            DateTimeQualifier.YearToSecond);

        value.ToString().Should().Be("2026-08-05 14:37:12");
    }
}
