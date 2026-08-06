using Ims.Core.Data;

namespace Ims.Core.Catalog;

/// <summary>One column of a table (PR-2.4).</summary>
public sealed record ColumnDetail
{
    public required int Position { get; init; }

    public required string Name { get; init; }

    public required InformixDbType DbType { get; init; }

    /// <summary>The type as a user would write it, e.g. <c>DECIMAL(10,2)</c>.</summary>
    public required string TypeDescription { get; init; }

    public required bool IsNullable { get; init; }

    /// <summary>Set for DATETIME and INTERVAL, decoded from the catalogue encoding.</summary>
    public DateTimeQualifier? Qualifier { get; init; }

    public int? Length { get; init; }

    public int? Precision { get; init; }

    public int? Scale { get; init; }

    public string? DefaultValue { get; init; }

    /// <summary>The raw <c>syscolumns.coltype</c>, shown on demand (PR-8.2).</summary>
    public required int RawColType { get; init; }

    /// <summary>The raw <c>syscolumns.collength</c>, shown on demand (PR-8.2).</summary>
    public required int RawColLength { get; init; }
}

/// <summary>One index (PR-2.4).</summary>
public sealed record IndexDetail
{
    public required string Name { get; init; }

    public required string Owner { get; init; }

    public required bool IsUnique { get; init; }

    public required bool IsClustered { get; init; }

    /// <summary>Column names in key order; a descending column is prefixed with a minus.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>True when this index exists to enforce a constraint rather than on its own.</summary>
    public bool BacksConstraint { get; init; }

    public int? Levels { get; init; }
}

/// <summary>What a constraint does.</summary>
public enum ConstraintKind
{
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,
    NotNull,
    Other,
}

/// <summary>One constraint (PR-2.4).</summary>
public sealed record ConstraintDetail
{
    public required string Name { get; init; }

    public required string Owner { get; init; }

    public required ConstraintKind Kind { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>For a foreign key: the table it references.</summary>
    public string? ReferencedTable { get; init; }

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>For a check constraint: the expression, where the catalogue exposes it.</summary>
    public string? CheckExpression { get; init; }

    /// <summary>The index backing this constraint, if any.</summary>
    public string? IndexName { get; init; }

    /// <summary>The raw <c>constrtype</c> character, shown on demand (PR-8.2).</summary>
    public required char RawConstraintType { get; init; }
}

/// <summary>One trigger (PR-2.4).</summary>
public sealed record TriggerDetail
{
    public required string Name { get; init; }

    public required string Owner { get; init; }

    /// <summary>INSERT, UPDATE, DELETE or SELECT.</summary>
    public required string Event { get; init; }

    /// <summary>The raw <c>systriggers.event</c> character (PR-8.2).</summary>
    public required char RawEvent { get; init; }
}

/// <summary>
/// How a table or index is distributed across dbspaces (PR-2.4).
/// </summary>
public sealed record FragmentDetail
{
    /// <summary>Round robin, expression, hash, interval or list.</summary>
    public required string Strategy { get; init; }

    /// <summary>The raw <c>sysfragments.strategy</c> character (PR-8.2).</summary>
    public required char RawStrategy { get; init; }

    public required string DbSpace { get; init; }

    /// <summary>The fragment expression, for an expression or interval strategy.</summary>
    public string? Expression { get; init; }

    public int? Position { get; init; }
}

/// <summary>Whether a table's statistics can be trusted (PR-2.5).</summary>
public enum StatisticsCurrency
{
    /// <summary>The server does not expose a timestamp, so IMS will not claim to know.</summary>
    Unknown,

    /// <summary>No statistics have been gathered.</summary>
    Never,

    Current,
    Stale,
}

/// <summary>
/// Everything PR-2.4 asks for about one table, in one object.
/// </summary>
/// <remarks>
/// PR-2.4 lists exactly this: "columns with types and nullability, indexes,
/// constraints, triggers, owner, estimated row count, dbspace placement, lock mode,
/// extent sizing, fragmentation strategy". The point is that a developer can answer
/// a question about a table without writing a catalogue query or reaching for
/// <c>dbschema</c>, which is G-2.
/// </remarks>
public sealed record TableDetail
{
    public required SchemaObject Object { get; init; }

    public required IReadOnlyList<ColumnDetail> Columns { get; init; }

    public required IReadOnlyList<IndexDetail> Indexes { get; init; }

    public required IReadOnlyList<ConstraintDetail> Constraints { get; init; }

    public required IReadOnlyList<TriggerDetail> Triggers { get; init; }

    public required IReadOnlyList<FragmentDetail> Fragments { get; init; }

    /// <summary>Page, row or table locking.</summary>
    public string LockMode { get; init; } = "Unknown";

    /// <summary>The raw <c>systables.locklevel</c> character (PR-8.2).</summary>
    public char RawLockLevel { get; init; }

    /// <summary>First extent size in kilobytes.</summary>
    public int? FirstExtentKb { get; init; }

    /// <summary>Subsequent extent size in kilobytes.</summary>
    public int? NextExtentKb { get; init; }

    /// <summary>Where the table lives when it is not fragmented.</summary>
    public string? DbSpace { get; init; }

    public long? EstimatedRows { get; init; }

    public StatisticsCurrency Statistics { get; init; } = StatisticsCurrency.Unknown;

    /// <summary>When statistics were last gathered, if the server says.</summary>
    public DateTime? StatisticsUpdatedAt { get; init; }

    /// <summary>Every catalogue query used to build this, for PR-8.2.</summary>
    public required IReadOnlyList<string> QueriesUsed { get; init; }

    public bool IsFragmented => Fragments.Count > 1;
}
