namespace Ims.Core.Data;

/// <summary>
/// Describes one column of a result set.
/// </summary>
/// <remarks>
/// Carries the Informix type and, where the type demands one, its qualifier —
/// which is what makes PR-4.5 rendering possible at the grid rather than
/// guesswork over a boxed CLR value.
/// </remarks>
public sealed record ResultColumn
{
    public required int Ordinal { get; init; }

    public required string Name { get; init; }

    public required InformixDbType DbType { get; init; }

    /// <summary>The server's own name for the type, shown verbatim (PR-8.2).</summary>
    public required string ServerTypeName { get; init; }

    /// <summary>Set for DATETIME and INTERVAL; null for every other type.</summary>
    public DateTimeQualifier? Qualifier { get; init; }

    /// <summary>Total digits for DECIMAL and MONEY.</summary>
    public int? Precision { get; init; }

    /// <summary>Digits after the point for DECIMAL and MONEY.</summary>
    public int? Scale { get; init; }

    /// <summary>Declared length for character types.</summary>
    public int? MaxLength { get; init; }

    public bool IsNullable { get; init; } = true;

    /// <summary>True for the types PR-4.5 says must not be rendered as bytes in a cell.</summary>
    public bool IsLargeObject =>
        DbType is InformixDbType.Byte or InformixDbType.Text
               or InformixDbType.Blob or InformixDbType.Clob;

    /// <summary>
    /// True where the values sort naturally as numbers rather than text — used by
    /// the grid to pick a comparer for PR-4.1.
    /// </summary>
    public bool IsNumeric =>
        DbType is InformixDbType.SmallInt or InformixDbType.Integer or InformixDbType.BigInt
               or InformixDbType.Serial or InformixDbType.Serial8 or InformixDbType.BigSerial
               or InformixDbType.Decimal or InformixDbType.Money
               or InformixDbType.SmallFloat or InformixDbType.Float;
}
