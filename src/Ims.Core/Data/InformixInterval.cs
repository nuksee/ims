using System.Globalization;
using System.Text;

namespace Ims.Core.Data;

/// <summary>
/// An Informix INTERVAL value, preserving its qualifier and its class.
/// </summary>
/// <remarks>
/// <para>
/// PR-4.5 requires INTERVAL to render correctly. There is no CLR equivalent:
/// <see cref="TimeSpan"/> cannot represent the year-month class at all, because
/// a month has no fixed length. Informix has two mutually incompatible interval
/// classes and refuses to convert between them, so IMS models both rather than
/// forcing everything through <see cref="TimeSpan"/> and quietly corrupting the
/// year-month case.
/// </para>
/// <para>
/// ODBC returns INTERVAL columns as strings, so <see cref="TryParse"/> is the
/// real entry point and is fully testable without a server.
/// </para>
/// </remarks>
public readonly record struct InformixInterval
{
    public InformixInterval(
        DateTimeQualifier qualifier,
        bool isNegative,
        int years = 0,
        int months = 0,
        int days = 0,
        int hours = 0,
        int minutes = 0,
        int seconds = 0,
        int fraction = 0)
    {
        Qualifier = qualifier;
        IsNegative = isNegative;
        Years = years;
        Months = months;
        Days = days;
        Hours = hours;
        Minutes = minutes;
        Seconds = seconds;
        Fraction = fraction;
    }

    public DateTimeQualifier Qualifier { get; }

    public bool IsNegative { get; }

    public int Years { get; }

    public int Months { get; }

    public int Days { get; }

    public int Hours { get; }

    public int Minutes { get; }

    public int Seconds { get; }

    /// <summary>Fractional seconds, as an integer in the qualifier's precision.</summary>
    public int Fraction { get; }

    /// <summary>
    /// True for the YEAR TO MONTH class. Informix will not convert between the
    /// two classes, and neither does IMS.
    /// </summary>
    public bool IsYearMonthClass => Qualifier.End <= DateTimeField.Month;

    /// <summary>
    /// Total months, signed. Only meaningful for the year-month class; used to
    /// give the result grid a sortable key (PR-4.1).
    /// </summary>
    public int TotalMonths
    {
        get
        {
            if (!IsYearMonthClass)
            {
                throw new InvalidOperationException(
                    "TotalMonths is only defined for the YEAR TO MONTH interval class.");
            }

            int total = (Years * 12) + Months;
            return IsNegative ? -total : total;
        }
    }

    /// <summary>
    /// The value as a <see cref="TimeSpan"/>. Only meaningful for the day-time
    /// class — the year-month class throws rather than inventing a month length.
    /// </summary>
    public TimeSpan ToTimeSpan()
    {
        if (IsYearMonthClass)
        {
            throw new InvalidOperationException(
                "A YEAR TO MONTH interval has no fixed duration and cannot become a TimeSpan.");
        }

        double fractionalSeconds = Qualifier.FractionalDigits == 0
            ? 0
            : Fraction / Math.Pow(10, Qualifier.FractionalDigits);

        var span = new TimeSpan(Days, Hours, Minutes, Seconds)
                   + TimeSpan.FromSeconds(fractionalSeconds);

        return IsNegative ? span.Negate() : span;
    }

    /// <summary>
    /// Parses the text form Informix and the ODBC driver use for the given qualifier.
    /// </summary>
    /// <remarks>
    /// The qualifier must be supplied because the text alone is ambiguous:
    /// <c>"2-06"</c> is two years six months under YEAR TO MONTH, and nothing at
    /// all under DAY TO SECOND.
    /// </remarks>
    public static bool TryParse(
        string? text,
        DateTimeQualifier qualifier,
        out InformixInterval interval)
    {
        interval = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        bool negative = false;
        if (span.Length > 0 && (span[0] == '-' || span[0] == '+'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        Span<int> values = stackalloc int[11];
        int position = 0;

        for (DateTimeField field = qualifier.Start; field <= qualifier.End; field++)
        {
            // Every field after the first is preceded by its separator.
            if (field != qualifier.Start)
            {
                char expected = SeparatorBefore(field);
                if (position >= span.Length || span[position] != expected)
                {
                    return false;
                }

                position++;
            }

            // Fractional digits are a single group, not one field per digit.
            if (field == DateTimeField.Fraction1)
            {
                int digits = qualifier.FractionalDigits;
                int start = position;
                while (position < span.Length && char.IsAsciiDigit(span[position]))
                {
                    position++;
                }

                if (position == start)
                {
                    return false;
                }

                ReadOnlySpan<char> raw = span[start..position];
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedFraction))
                {
                    return false;
                }

                // Informix pads to the qualifier's precision; "5" at FRACTION(3) is 500.
                for (int i = raw.Length; i < digits; i++)
                {
                    parsedFraction *= 10;
                }

                values[(int)DateTimeField.Fraction1] = parsedFraction;
                field = qualifier.End; // the fraction group consumed the remaining fields
                continue;
            }

            int digitStart = position;
            while (position < span.Length && char.IsAsciiDigit(span[position]))
            {
                position++;
            }

            if (position == digitStart)
            {
                return false;
            }

            if (!int.TryParse(
                    span[digitStart..position],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value))
            {
                return false;
            }

            values[(int)field] = value;
        }

        // Trailing content means this was not the value we were told to expect.
        if (position != span.Length)
        {
            return false;
        }

        interval = new InformixInterval(
            qualifier,
            negative,
            years: values[(int)DateTimeField.Year],
            months: values[(int)DateTimeField.Month],
            days: values[(int)DateTimeField.Day],
            hours: values[(int)DateTimeField.Hour],
            minutes: values[(int)DateTimeField.Minute],
            seconds: values[(int)DateTimeField.Second],
            fraction: values[(int)DateTimeField.Fraction1]);

        return true;
    }

    /// <summary>
    /// Renders the value the way Informix writes it, so that what the grid shows
    /// can be pasted straight back into a statement (PR-8.2).
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();

        if (IsNegative)
        {
            builder.Append('-');
        }

        for (DateTimeField field = Qualifier.Start; field <= Qualifier.End; field++)
        {
            if (field == DateTimeField.Fraction1)
            {
                builder.Append('.')
                       .Append(Fraction.ToString(
                           new string('0', Qualifier.FractionalDigits),
                           CultureInfo.InvariantCulture));
                break;
            }

            if (field != Qualifier.Start)
            {
                builder.Append(SeparatorBefore(field));
            }

            builder.Append(ComponentFor(field).ToString(
                field == Qualifier.Start ? "0" : "00",
                CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private int ComponentFor(DateTimeField field) => field switch
    {
        DateTimeField.Year => Years,
        DateTimeField.Month => Months,
        DateTimeField.Day => Days,
        DateTimeField.Hour => Hours,
        DateTimeField.Minute => Minutes,
        DateTimeField.Second => Seconds,
        _ => 0,
    };

    private static char SeparatorBefore(DateTimeField field) => field switch
    {
        DateTimeField.Month => '-',
        DateTimeField.Day => '-',
        DateTimeField.Hour => ' ',
        DateTimeField.Minute => ':',
        DateTimeField.Second => ':',
        _ => '.',
    };
}
