using FluentAssertions;
using Ims.Core.Data;
using Xunit;

namespace Ims.Core.Tests;

public class InformixIntervalTests
{
    private static readonly DateTimeQualifier YearToMonth =
        new(DateTimeField.Year, DateTimeField.Month);

    private static readonly DateTimeQualifier DayToSecond =
        new(DateTimeField.Day, DateTimeField.Second);

    private static readonly DateTimeQualifier DayToFraction3 =
        new(DateTimeField.Day, DateTimeField.Fraction3);

    [Fact]
    public void Parses_the_year_month_class()
    {
        InformixInterval.TryParse("2-06", YearToMonth, out InformixInterval interval)
            .Should().BeTrue();

        interval.Years.Should().Be(2);
        interval.Months.Should().Be(6);
        interval.IsYearMonthClass.Should().BeTrue();
        interval.TotalMonths.Should().Be(30);
    }

    [Fact]
    public void Parses_the_day_time_class()
    {
        InformixInterval.TryParse("5 12:30:45", DayToSecond, out InformixInterval interval)
            .Should().BeTrue();

        interval.Days.Should().Be(5);
        interval.Hours.Should().Be(12);
        interval.Minutes.Should().Be(30);
        interval.Seconds.Should().Be(45);
        interval.IsYearMonthClass.Should().BeFalse();
        interval.ToTimeSpan().Should().Be(new TimeSpan(5, 12, 30, 45));
    }

    [Fact]
    public void Parses_a_negative_interval()
    {
        InformixInterval.TryParse("-5 12:30:45", DayToSecond, out InformixInterval interval)
            .Should().BeTrue();

        interval.IsNegative.Should().BeTrue();
        interval.ToTimeSpan().Should().Be(new TimeSpan(5, 12, 30, 45).Negate());
    }

    [Fact]
    public void Pads_a_short_fraction_to_the_declared_precision()
    {
        // Informix writes the fraction at the qualifier's precision, so "5" under
        // FRACTION(3) is 500 milliseconds, not 5.
        InformixInterval.TryParse("1 00:00:00.5", DayToFraction3, out InformixInterval interval)
            .Should().BeTrue();

        interval.Fraction.Should().Be(500);
        interval.ToTimeSpan().Should().Be(new TimeSpan(1, 0, 0, 0, 500));
    }

    [Fact]
    public void Rejects_text_that_does_not_match_the_qualifier()
    {
        // "2-06" is a valid YEAR TO MONTH interval and nothing at all under DAY TO SECOND.
        InformixInterval.TryParse("2-06", DayToSecond, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_trailing_content()
    {
        InformixInterval.TryParse("5 12:30:45 extra", DayToSecond, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an interval")]
    public void Rejects_junk(string? text)
    {
        InformixInterval.TryParse(text, DayToSecond, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("2-06")]
    [InlineData("-2-06")]
    public void Round_trips_the_year_month_class(string text)
    {
        InformixInterval.TryParse(text, YearToMonth, out InformixInterval interval)
            .Should().BeTrue();

        interval.ToString().Should().Be(text);
    }

    [Theory]
    [InlineData("5 12:30:45")]
    [InlineData("-5 12:30:45")]
    public void Round_trips_the_day_time_class(string text)
    {
        InformixInterval.TryParse(text, DayToSecond, out InformixInterval interval)
            .Should().BeTrue();

        interval.ToString().Should().Be(text);
    }

    [Fact]
    public void A_year_month_interval_refuses_to_become_a_TimeSpan()
    {
        // A month has no fixed length, and Informix will not convert between the two
        // interval classes either. Silently picking 30 days would corrupt the value.
        InformixInterval.TryParse("2-06", YearToMonth, out InformixInterval interval)
            .Should().BeTrue();

        interval.Invoking(i => i.ToTimeSpan())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_day_time_interval_has_no_total_months()
    {
        InformixInterval.TryParse("5 12:30:45", DayToSecond, out InformixInterval interval)
            .Should().BeTrue();

        interval.Invoking(i => i.TotalMonths)
            .Should().Throw<InvalidOperationException>();
    }
}
