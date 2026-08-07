namespace Ims.Core.Data;

/// <summary>
/// One cell of a result set, carrying its Informix type and an unambiguous null state.
/// </summary>
/// <remarks>
/// <para>
/// PR-4.4 requires NULL to be distinguishable from an empty string and from zero.
/// That sounds trivial and is the usual place tools get it wrong: once a value has
/// been boxed to <see cref="object"/> and a null has become <see cref="DBNull"/>
/// or <c>""</c> somewhere in the pipeline, the distinction cannot be recovered at
/// the grid. So nullness is carried as its own flag, alongside the type, from the
/// moment the row is read.
/// </para>
/// <para>
/// A struct, because there is one of these per cell and PR-4.2 expects result sets
/// of a million rows to stream without undue allocation.
/// </para>
/// </remarks>
public readonly struct InformixValue : IEquatable<InformixValue>
{
    private InformixValue(InformixDbType dbType, object? value, bool isNull)
    {
        DbType = dbType;
        _value = value;
        IsNull = isNull;
    }

    private readonly object? _value;

    /// <summary>The Informix type of the column this value came from.</summary>
    public InformixDbType DbType { get; }

    /// <summary>True when the server returned SQL NULL. Never inferred from the value.</summary>
    public bool IsNull { get; }

    /// <summary>
    /// The underlying value, or null when <see cref="IsNull"/>. Prefer the typed
    /// accessors; this exists for the export and formatting paths.
    /// </summary>
    public object? Value => IsNull ? null : _value;

    /// <summary>A SQL NULL of a known type.</summary>
    public static InformixValue Null(InformixDbType dbType) => new(dbType, null, isNull: true);

    /// <summary>A present value of a known type.</summary>
    public static InformixValue From(InformixDbType dbType, object? value) =>
        value is null or DBNull
            ? Null(dbType)
            : new InformixValue(dbType, value, isNull: false);

    /// <summary>A DATETIME, which is meaningless without its qualifier.</summary>
    public static InformixValue DateTime(DateTime value, DateTimeQualifier qualifier) =>
        new(InformixDbType.DateTime, new QualifiedDateTime(value, qualifier), isNull: false);

    /// <summary>An INTERVAL, in either class.</summary>
    public static InformixValue Interval(InformixInterval value) =>
        new(InformixDbType.Interval, value, isNull: false);

    /// <summary>A large object, unfetched (PR-4.5).</summary>
    public static InformixValue LargeObject(LargeObjectReference reference) =>
        new(reference.DbType, reference, isNull: false);

    public bool TryGetDateTime(out QualifiedDateTime value)
    {
        if (!IsNull && _value is QualifiedDateTime qualified)
        {
            value = qualified;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetInterval(out InformixInterval value)
    {
        if (!IsNull && _value is InformixInterval interval)
        {
            value = interval;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetLargeObject(out LargeObjectReference? reference)
    {
        if (!IsNull && _value is LargeObjectReference large)
        {
            reference = large;
            return true;
        }

        reference = null;
        return false;
    }

    /// <summary>
    /// The value as text for display or export.
    /// </summary>
    /// <param name="nullRepresentation">
    /// How to render SQL NULL. The grid passes a visually distinct marker (PR-4.4);
    /// CSV export passes an empty field, because a literal "NULL" in a CSV is a
    /// value, not an absence.
    /// </param>
    public string ToDisplayString(string nullRepresentation = "(null)")
    {
        if (IsNull)
        {
            return nullRepresentation;
        }

        return _value switch
        {
            QualifiedDateTime qualified => qualified.ToString(),
            InformixInterval interval => interval.ToString(),
            LargeObjectReference large => large.Placeholder,
            bool boolean => boolean ? "t" : "f", // Informix's own BOOLEAN literals
            IFormattable formattable => formattable.ToString(
                null, System.Globalization.CultureInfo.CurrentCulture),
            _ => _value?.ToString() ?? string.Empty,
        };
    }

    public bool Equals(InformixValue other) =>
        IsNull == other.IsNull
        && DbType == other.DbType
        && Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is InformixValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(DbType, IsNull, _value);

    public override string ToString() => ToDisplayString();

    public static bool operator ==(InformixValue left, InformixValue right) => left.Equals(right);

    public static bool operator !=(InformixValue left, InformixValue right) => !left.Equals(right);
}

/// <summary>
/// A DATETIME bound to the qualifier that gives it meaning (PR-4.5).
/// </summary>
public readonly record struct QualifiedDateTime(DateTime Value, DateTimeQualifier Qualifier)
{
    /// <summary>
    /// Renders to exactly the precision the column declares — so a
    /// <c>DATETIME YEAR TO DAY</c> shows no time, rather than a misleading 00:00:00.
    /// </summary>
    public override string ToString() =>
        Value.ToString(Qualifier.ToFormatString(), System.Globalization.CultureInfo.InvariantCulture);
}
