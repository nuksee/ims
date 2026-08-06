using System.Globalization;
using Ims.Core.Data;

namespace Ims.Data.Informix;

/// <summary>
/// Translates between Informix's type system and the values ODBC hands back.
/// </summary>
/// <remarks>
/// <para>
/// The cost of taking the ODBC branch of DEC-4. The CSDK's managed provider would
/// have supplied <c>IfxDateTime</c>, <c>IfxTimeSpan</c> and <c>IfxDecimal</c>, but
/// it targets .NET Framework 2.0 and cannot load in .NET 9, so this class does the
/// work instead. It is a first-class component rather than a helper because PR-4.5
/// is a Must and this is the only place it can be satisfied.
/// </para>
/// <para>
/// Everything here is a pure function of its inputs, so all of it is testable
/// without a server — which matters, since the acceptance criteria that need one
/// cannot be run yet.
/// </para>
/// </remarks>
public static class InformixTypeMapper
{
    /// <summary>
    /// Maps a <c>syscolumns.coltype</c> code to an Informix type.
    /// </summary>
    /// <remarks>
    /// The catalogue adds 256 to the code when the column is NOT NULL, so the low
    /// byte is the type and the flag is recovered separately by
    /// <see cref="IsNotNullFromCatalog"/>.
    /// </remarks>
    public static InformixDbType FromCatalogTypeCode(int coltype) => (coltype & 0xFF) switch
    {
        0 => InformixDbType.Char,
        1 => InformixDbType.SmallInt,
        2 => InformixDbType.Integer,
        3 => InformixDbType.Float,
        4 => InformixDbType.SmallFloat,
        5 => InformixDbType.Decimal,
        6 => InformixDbType.Serial,
        7 => InformixDbType.Date,
        8 => InformixDbType.Money,
        10 => InformixDbType.DateTime,
        11 => InformixDbType.Byte,
        12 => InformixDbType.Text,
        13 => InformixDbType.VarChar,
        14 => InformixDbType.Interval,
        15 => InformixDbType.NChar,
        16 => InformixDbType.NVarChar,
        17 => InformixDbType.BigInt,     // INT8
        18 => InformixDbType.Serial8,
        19 => InformixDbType.Set,
        20 => InformixDbType.Multiset,
        21 => InformixDbType.List,
        22 => InformixDbType.Row,
        23 => InformixDbType.Row,        // COLLECTION
        40 => InformixDbType.Other,      // variable-length opaque; resolved via sysxtdtypes
        41 => InformixDbType.Other,      // fixed-length opaque: BLOB, CLOB or BOOLEAN
        43 => InformixDbType.LVarChar,
        45 => InformixDbType.Boolean,
        52 => InformixDbType.BigInt,
        53 => InformixDbType.BigSerial,
        _ => InformixDbType.Unknown,
    };

    /// <summary>
    /// True when a <c>syscolumns.coltype</c> value carries the NOT NULL flag.
    /// </summary>
    public static bool IsNotNullFromCatalog(int coltype) => (coltype & 0x100) != 0;

    /// <summary>
    /// True for the opaque catalogue codes that need a <c>sysxtdtypes</c> lookup
    /// before the real type is known.
    /// </summary>
    /// <remarks>
    /// Codes 40 and 41 cover BLOB, CLOB, BOOLEAN and every user-defined type, and
    /// the code alone cannot tell them apart. Slice 2's catalogue layer resolves
    /// them by extended id; until then, saying "Other" is honest and guessing
    /// would not be (PR-8.4).
    /// </remarks>
    public static bool RequiresExtendedTypeLookup(int coltype) => (coltype & 0xFF) is 40 or 41;

    /// <summary>
    /// Maps a type name — as ODBC's <c>GetDataTypeName</c> or the catalogue reports
    /// it — to an Informix type.
    /// </summary>
    public static InformixDbType FromServerTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return InformixDbType.Unknown;
        }

        // "DATETIME YEAR TO SECOND" and "DECIMAL(10,2)" both carry a suffix.
        string name = typeName.Trim();

        int cut = name.IndexOfAny([' ', '(']);
        if (cut > 0)
        {
            name = name[..cut];
        }

        return name.ToUpperInvariant() switch
        {
            "CHAR" or "CHARACTER" => InformixDbType.Char,
            "VARCHAR" or "CHARACTER VARYING" => InformixDbType.VarChar,
            "LVARCHAR" => InformixDbType.LVarChar,
            "NCHAR" => InformixDbType.NChar,
            "NVARCHAR" => InformixDbType.NVarChar,
            "SMALLINT" => InformixDbType.SmallInt,
            "INT" or "INTEGER" => InformixDbType.Integer,
            "INT8" or "BIGINT" => InformixDbType.BigInt,
            "SERIAL" => InformixDbType.Serial,
            "SERIAL8" => InformixDbType.Serial8,
            "BIGSERIAL" => InformixDbType.BigSerial,
            "DECIMAL" or "DEC" or "NUMERIC" => InformixDbType.Decimal,
            "MONEY" => InformixDbType.Money,
            "SMALLFLOAT" or "REAL" => InformixDbType.SmallFloat,
            "FLOAT" or "DOUBLE" => InformixDbType.Float,
            "DATE" => InformixDbType.Date,
            "DATETIME" or "TIMESTAMP" => InformixDbType.DateTime,
            "INTERVAL" => InformixDbType.Interval,
            "BOOLEAN" => InformixDbType.Boolean,
            "BYTE" => InformixDbType.Byte,
            "TEXT" => InformixDbType.Text,
            "BLOB" => InformixDbType.Blob,
            "CLOB" => InformixDbType.Clob,
            "SET" => InformixDbType.Set,
            "MULTISET" => InformixDbType.Multiset,
            "LIST" => InformixDbType.List,
            "ROW" => InformixDbType.Row,
            _ => InformixDbType.Other,
        };
    }

    // ---- DATETIME and INTERVAL qualifiers -------------------------------------
    //
    // Informix encodes a qualifier into syscolumns.collength as
    //
    //     collength = (digits * 256) + (start_unit * 16) + end_unit
    //
    // with time units YEAR=0, MONTH=2, DAY=4, HOUR=6, MINUTE=8, SECOND=10 and
    // FRACTION(1..5)=11..15. DATETIME YEAR TO SECOND is 3594; YEAR TO FRACTION(3)
    // is 4365. Decoding it is the only way to recover the qualifier PR-4.5 needs,
    // because ODBC reports every DATETIME as a plain timestamp.

    /// <summary>Decodes a <c>syscolumns.collength</c> into its qualifier.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a valid qualifier.</exception>
    public static DateTimeQualifier DecodeCatalogQualifier(int collength)
    {
        int startUnit = (collength % 256) / 16;
        int endUnit = collength % 16;

        if (!TryFromTimeUnit(startUnit, out DateTimeField start))
        {
            throw new ArgumentOutOfRangeException(
                nameof(collength),
                collength,
                $"Start time unit {startUnit} is not a valid Informix qualifier field.");
        }

        if (!TryFromTimeUnit(endUnit, out DateTimeField end))
        {
            throw new ArgumentOutOfRangeException(
                nameof(collength),
                collength,
                $"End time unit {endUnit} is not a valid Informix qualifier field.");
        }

        return new DateTimeQualifier(start, end);
    }

    /// <summary>Encodes a qualifier the way the catalogue stores it.</summary>
    public static int EncodeCatalogQualifier(DateTimeQualifier qualifier) =>
        (DigitsFor(qualifier) * 256)
        + (ToTimeUnit(qualifier.Start) * 16)
        + ToTimeUnit(qualifier.End);

    /// <summary>
    /// Total digits across the fields a qualifier spans — the <c>digits</c> term
    /// of the catalogue encoding.
    /// </summary>
    public static int DigitsFor(DateTimeQualifier qualifier)
    {
        int digits = 0;

        for (DateTimeField field = qualifier.Start; field <= qualifier.End; field++)
        {
            // The FRACTION fields are one group, not one field per digit, and the
            // group's width is the end field's — FRACTION(3) contributes 3, not 1.
            if (field >= DateTimeField.Fraction1)
            {
                digits += qualifier.FractionalDigits;
                break;
            }

            digits += field == DateTimeField.Year ? 4 : 2;
        }

        return digits;
    }

    /// <summary>
    /// Parses a written qualifier such as <c>"YEAR TO FRACTION(3)"</c>, with or
    /// without a leading <c>DATETIME</c> or <c>INTERVAL</c>.
    /// </summary>
    public static bool TryParseQualifier(string? text, out DateTimeQualifier qualifier)
    {
        qualifier = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalised = text.Trim().ToUpperInvariant();

        foreach (string prefix in (string[])["DATETIME", "INTERVAL"])
        {
            if (normalised.StartsWith(prefix, StringComparison.Ordinal))
            {
                normalised = normalised[prefix.Length..].Trim();
            }
        }

        string[] parts = normalised.Split(" TO ", StringSplitOptions.TrimEntries);

        if (parts.Length is < 1 or > 2 || parts[0].Length == 0)
        {
            return false;
        }

        if (!TryParseField(parts[0], out DateTimeField start))
        {
            return false;
        }

        DateTimeField end = start;
        if (parts.Length == 2 && !TryParseField(parts[1], out end))
        {
            return false;
        }

        if (end < start)
        {
            return false;
        }

        qualifier = new DateTimeQualifier(start, end);
        return true;
    }

    /// <summary>
    /// Converts a raw ODBC value into an <see cref="InformixValue"/> that keeps its
    /// type and its null state.
    /// </summary>
    /// <remarks>
    /// PR-4.4 lives here: a null becomes a typed <see cref="InformixValue.Null"/>
    /// rather than a bare null or an empty string, so the grid can still tell the
    /// three apart at render time.
    /// </remarks>
    public static InformixValue ToInformixValue(ResultColumn column, object? raw)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (raw is null or DBNull)
        {
            return InformixValue.Null(column.DbType);
        }

        switch (column.DbType)
        {
            case InformixDbType.DateTime:
            {
                DateTimeQualifier qualifier = column.Qualifier ?? DateTimeQualifier.YearToFraction3;

                if (raw is DateTime dateTime)
                {
                    return InformixValue.DateTime(dateTime, qualifier);
                }

                // Some qualifiers come back as text; keep the value rather than lose it.
                if (raw is string dateText
                    && DateTime.TryParse(
                        dateText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime parsed))
                {
                    return InformixValue.DateTime(parsed, qualifier);
                }

                break;
            }

            case InformixDbType.Interval:
            {
                // ODBC returns INTERVAL as text, which is why InformixInterval.TryParse
                // is the real entry point for this type.
                DateTimeQualifier qualifier = column.Qualifier
                    ?? new DateTimeQualifier(DateTimeField.Day, DateTimeField.Second);

                if (raw is string intervalText
                    && InformixInterval.TryParse(intervalText, qualifier, out InformixInterval interval))
                {
                    return InformixValue.Interval(interval);
                }

                break;
            }

            case InformixDbType.Boolean:
            {
                // Informix BOOLEAN arrives as 't'/'f' as often as as a bool.
                if (raw is string booleanText && booleanText.Length > 0)
                {
                    char first = char.ToLowerInvariant(booleanText[0]);
                    if (first is 't' or 'f')
                    {
                        return InformixValue.From(InformixDbType.Boolean, first == 't');
                    }
                }

                break;
            }
        }

        return InformixValue.From(column.DbType, raw);
    }

    private static bool TryParseField(string text, out DateTimeField field)
    {
        // The driver reports a leading-field precision, e.g. "INTERVAL DAY(2) TO
        // SECOND". The precision does not change which field it is, so it is
        // stripped — except for FRACTION, where the number selects the field.
        if (!text.StartsWith("FRACTION", StringComparison.Ordinal))
        {
            int parenthesis = text.IndexOf('(', StringComparison.Ordinal);

            if (parenthesis > 0)
            {
                text = text[..parenthesis].TrimEnd();
            }
        }

        switch (text)
        {
            case "YEAR": field = DateTimeField.Year; return true;
            case "MONTH": field = DateTimeField.Month; return true;
            case "DAY": field = DateTimeField.Day; return true;
            case "HOUR": field = DateTimeField.Hour; return true;
            case "MINUTE": field = DateTimeField.Minute; return true;
            case "SECOND": field = DateTimeField.Second; return true;
            case "FRACTION": field = DateTimeField.Fraction3; return true; // Informix's default
            case "FRACTION(1)": field = DateTimeField.Fraction1; return true;
            case "FRACTION(2)": field = DateTimeField.Fraction2; return true;
            case "FRACTION(3)": field = DateTimeField.Fraction3; return true;
            case "FRACTION(4)": field = DateTimeField.Fraction4; return true;
            case "FRACTION(5)": field = DateTimeField.Fraction5; return true;
            default: field = default; return false;
        }
    }

    private static bool TryFromTimeUnit(int unit, out DateTimeField field)
    {
        switch (unit)
        {
            case 0: field = DateTimeField.Year; return true;
            case 2: field = DateTimeField.Month; return true;
            case 4: field = DateTimeField.Day; return true;
            case 6: field = DateTimeField.Hour; return true;
            case 8: field = DateTimeField.Minute; return true;
            case 10: field = DateTimeField.Second; return true;
            case 11: field = DateTimeField.Fraction1; return true;
            case 12: field = DateTimeField.Fraction2; return true;
            case 13: field = DateTimeField.Fraction3; return true;
            case 14: field = DateTimeField.Fraction4; return true;
            case 15: field = DateTimeField.Fraction5; return true;
            default: field = default; return false;
        }
    }

    private static int ToTimeUnit(DateTimeField field) => field switch
    {
        DateTimeField.Year => 0,
        DateTimeField.Month => 2,
        DateTimeField.Day => 4,
        DateTimeField.Hour => 6,
        DateTimeField.Minute => 8,
        DateTimeField.Second => 10,
        DateTimeField.Fraction1 => 11,
        DateTimeField.Fraction2 => 12,
        DateTimeField.Fraction3 => 13,
        DateTimeField.Fraction4 => 14,
        DateTimeField.Fraction5 => 15,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a qualifier field."),
    };
}
