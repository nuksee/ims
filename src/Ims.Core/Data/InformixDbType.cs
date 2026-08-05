namespace Ims.Core.Data;

/// <summary>
/// Informix's own type system, preserved rather than flattened to a CLR type.
/// </summary>
/// <remarks>
/// PR-4.5 requires the grid to render Informix types correctly, which is only
/// possible if the provider layer reports what the column actually is. ODBC
/// hands back a generic SQL type and, for several Informix types, a string —
/// so this enum plus the column's qualifier is the information the grid needs
/// and the ODBC layer would otherwise throw away.
/// </remarks>
public enum InformixDbType
{
    Unknown = 0,

    // Exact numerics
    SmallInt,
    Integer,
    BigInt,
    Serial,
    Serial8,
    BigSerial,
    Decimal,

    /// <summary>DECIMAL with a currency presentation. Distinct from Decimal for PR-4.5.</summary>
    Money,

    // Approximate numerics
    SmallFloat,
    Float,

    // Character
    Char,
    VarChar,
    LVarChar,
    NChar,
    NVarChar,

    // Temporal
    Date,

    /// <summary>DATETIME. Meaningless without its qualifier — see <see cref="DateTimeQualifier"/>.</summary>
    DateTime,

    /// <summary>INTERVAL. Also qualifier-bearing, and in one of two incompatible classes.</summary>
    Interval,

    Boolean,

    // Large objects. PR-4.5: shown as a viewable value, never raw bytes in a cell.
    Byte,
    Text,
    Blob,
    Clob,

    // Collections and complex types
    Set,
    Multiset,
    List,
    Row,

    /// <summary>An opaque or user-defined type IMS has no special handling for.</summary>
    Other,
}

/// <summary>
/// The fields an Informix DATETIME or INTERVAL qualifier can start or end at.
/// </summary>
/// <remarks>
/// Ordering matters and is significant: Informix qualifiers are a contiguous
/// range from a start field to an end field, so comparisons on these values are
/// used to validate and format a qualifier.
/// </remarks>
public enum DateTimeField
{
    Year = 0,
    Month = 1,
    Day = 2,
    Hour = 3,
    Minute = 4,
    Second = 5,
    Fraction1 = 6,
    Fraction2 = 7,
    Fraction3 = 8,
    Fraction4 = 9,
    Fraction5 = 10,
}
