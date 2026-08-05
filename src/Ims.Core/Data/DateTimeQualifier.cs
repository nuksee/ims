namespace Ims.Core.Data;

/// <summary>
/// An Informix DATETIME or INTERVAL qualifier — the contiguous field range that
/// gives the value its meaning and precision.
/// </summary>
/// <remarks>
/// <para>
/// PR-4.5 names "DATETIME with its qualifier" specifically, because a DATETIME
/// without one is ambiguous: <c>DATETIME YEAR TO DAY</c> and
/// <c>DATETIME YEAR TO FRACTION(5)</c> are different types that ODBC will happily
/// hand back as the same <see cref="System.DateTime"/>. Dropping the qualifier
/// loses information the user needs to see, and loses it silently.
/// </para>
/// </remarks>
public readonly record struct DateTimeQualifier
{
    /// <summary>The most common DATETIME qualifier, and Informix's own default for CURRENT.</summary>
    public static readonly DateTimeQualifier YearToFraction3 =
        new(DateTimeField.Year, DateTimeField.Fraction3);

    /// <summary>Equivalent in precision to a plain DATE.</summary>
    public static readonly DateTimeQualifier YearToDay =
        new(DateTimeField.Year, DateTimeField.Day);

    public static readonly DateTimeQualifier YearToSecond =
        new(DateTimeField.Year, DateTimeField.Second);

    public DateTimeQualifier(DateTimeField start, DateTimeField end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                $"An Informix qualifier runs from a larger field to a smaller one; '{start} TO {end}' is inverted.");
        }

        Start = start;
        End = end;
    }

    public DateTimeField Start { get; }

    public DateTimeField End { get; }

    /// <summary>
    /// Number of fractional-second digits this qualifier carries, 0 when it ends
    /// at SECOND or coarser.
    /// </summary>
    public int FractionalDigits =>
        End >= DateTimeField.Fraction1 ? End - DateTimeField.Fraction1 + 1 : 0;

    /// <summary>True when the range covers no time-of-day fields at all.</summary>
    public bool IsDateOnly => End <= DateTimeField.Day;

    /// <summary>
    /// Renders the qualifier as Informix would write it, e.g. <c>YEAR TO FRACTION(3)</c>.
    /// </summary>
    public override string ToString() =>
        Start == End ? FieldName(Start) : $"{FieldName(Start)} TO {FieldName(End)}";

    /// <summary>
    /// A .NET format string that renders a value of this precision and no more.
    /// Used by the grid so a YEAR TO DAY column does not display a spurious 00:00:00.
    /// </summary>
    public string ToFormatString()
    {
        string format = Start switch
        {
            DateTimeField.Year => "yyyy",
            DateTimeField.Month => "MM",
            DateTimeField.Day => "dd",
            DateTimeField.Hour => "HH",
            DateTimeField.Minute => "mm",
            DateTimeField.Second => "ss",
            _ => "yyyy",
        };

        // Append each field between Start and End, with the separator Informix uses.
        for (DateTimeField f = Start + 1; f <= End; f++)
        {
            format += f switch
            {
                DateTimeField.Month => "-MM",
                DateTimeField.Day => "-dd",
                DateTimeField.Hour => " HH",
                DateTimeField.Minute => ":mm",
                DateTimeField.Second => ":ss",
                DateTimeField.Fraction1 => ".f",
                DateTimeField.Fraction2 or DateTimeField.Fraction3 or
                DateTimeField.Fraction4 or DateTimeField.Fraction5 => "f",
                _ => string.Empty,
            };
        }

        return format;
    }

    private static string FieldName(DateTimeField field) => field switch
    {
        DateTimeField.Year => "YEAR",
        DateTimeField.Month => "MONTH",
        DateTimeField.Day => "DAY",
        DateTimeField.Hour => "HOUR",
        DateTimeField.Minute => "MINUTE",
        DateTimeField.Second => "SECOND",
        DateTimeField.Fraction1 => "FRACTION(1)",
        DateTimeField.Fraction2 => "FRACTION(2)",
        DateTimeField.Fraction3 => "FRACTION(3)",
        DateTimeField.Fraction4 => "FRACTION(4)",
        DateTimeField.Fraction5 => "FRACTION(5)",
        _ => field.ToString().ToUpperInvariant(),
    };
}
