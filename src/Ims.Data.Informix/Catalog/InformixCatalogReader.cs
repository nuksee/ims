using System.Data.Odbc;
using System.Globalization;
using System.Text;
using Ims.Core.Catalog;
using Ims.Core.Data;
using Ims.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ims.Data.Informix.Catalog;

/// <summary>
/// Reads schema metadata from Informix's system catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Holds its own connection rather than sharing the editor's session, and that is
/// deliberate. A connection has one cursor, so a catalogue query issued on the
/// editor's session would close whatever result the user was looking at. The cost
/// is a second server session per connected instance; PR-6.4 requires these queries
/// to stay light enough that this is negligible, which is why each is small,
/// indexed on <c>tabid</c>, and issued only when the user expands or selects
/// something.
/// </para>
/// <para>
/// Every sub-query of <see cref="GetTableDetailAsync"/> is independently
/// failure-tolerant. A catalogue table that is missing, restricted or shaped
/// differently on some version should cost its own section of the detail pane and
/// nothing else — that is NFR-4's "degrade gracefully with a clear explanation
/// rather than failing opaquely", applied at the smallest useful granularity.
/// </para>
/// </remarks>
public sealed class InformixCatalogReader : ICatalogReader, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private OdbcConnection? _connection;

    /// <summary>Null until probed; NFR-4 capability detection for PR-2.5.</summary>
    private bool? _hasStatisticsTimestamp;

    public InformixCatalogReader(
        string connectionString,
        ILogger<InformixCatalogReader>? logger = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? NullLogger<InformixCatalogReader>.Instance;
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Open the catalogue connection");

        _connection = new OdbcConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    // ---- Listing ---------------------------------------------------------------

    public async Task<CatalogResult<DatabaseInfo>> GetDatabasesAsync(CancellationToken cancellationToken)
    {
        const string sql = CatalogQueries.Databases;

        var databases = await QueryAsync(
            sql,
            reader => new DatabaseInfo
            {
                Name = GetString(reader, 0) ?? string.Empty,
                Owner = GetString(reader, 1) ?? string.Empty,
                Logging = DescribeLogging(
                    GetBoolean(reader, 2),
                    GetBoolean(reader, 3),
                    GetBoolean(reader, 4)),
                IsAnsi = GetBoolean(reader, 4),
            },
            cancellationToken).ConfigureAwait(false);

        return new CatalogResult<DatabaseInfo>(databases, sql);
    }

    public async Task<CatalogResult<SchemaObject>> GetObjectsAsync(
        SchemaObjectKind kind,
        string? nameFilter,
        string? owner,
        bool includeSystem,
        CancellationToken cancellationToken)
    {
        string? pattern = string.IsNullOrWhiteSpace(nameFilter)
            ? null
            : "%" + nameFilter.Trim().ToLowerInvariant() + "%";

        return kind switch
        {
            SchemaObjectKind.Table or SchemaObjectKind.View or SchemaObjectKind.Synonym
                or SchemaObjectKind.PrivateSynonym or SchemaObjectKind.Sequence =>
                await GetTableLikeAsync(kind, pattern, owner, includeSystem, cancellationToken)
                    .ConfigureAwait(false),

            SchemaObjectKind.Procedure or SchemaObjectKind.Function =>
                await GetRoutinesAsync(kind, pattern, owner, includeSystem, cancellationToken)
                    .ConfigureAwait(false),

            SchemaObjectKind.Index =>
                await GetIndexesAsync(pattern, includeSystem, cancellationToken).ConfigureAwait(false),

            SchemaObjectKind.UserDefinedType =>
                await GetUserDefinedTypesAsync(pattern, includeSystem, cancellationToken)
                    .ConfigureAwait(false),

            _ => CatalogResult.Empty<SchemaObject>(string.Empty),
        };
    }

    private async Task<CatalogResult<SchemaObject>> GetTableLikeAsync(
        SchemaObjectKind kind,
        string? pattern,
        string? owner,
        bool includeSystem,
        CancellationToken cancellationToken)
    {
        string sql = CatalogQueries.ObjectsByType(includeSystem, pattern, owner);

        var parameters = new List<object?> { TabTypeFor(kind) };

        if (pattern is not null)
        {
            parameters.Add(pattern);
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            parameters.Add(owner);
        }

        var objects = await QueryAsync(
            sql,
            reader => new SchemaObject
            {
                TabId = GetInt(reader, 0) ?? 0,
                Name = (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                Owner = (GetString(reader, 2) ?? string.Empty).TrimEnd(),
                Kind = kind,
                EstimatedRows = GetLong(reader, 3),
                Created = GetDateTime(reader, 4),
            },
            cancellationToken,
            parameters.ToArray()).ConfigureAwait(false);

        return new CatalogResult<SchemaObject>(objects, sql);
    }

    private async Task<CatalogResult<SchemaObject>> GetRoutinesAsync(
        SchemaObjectKind kind,
        string? pattern,
        string? owner,
        bool includeSystem,
        CancellationToken cancellationToken)
    {
        string sql = CatalogQueries.Routines(includeSystem, pattern, owner);

        var parameters = new List<object?> { kind == SchemaObjectKind.Procedure ? "t" : "f" };

        if (pattern is not null)
        {
            parameters.Add(pattern);
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            parameters.Add(owner);
        }

        var objects = await QueryAsync(
            sql,
            reader => new SchemaObject
            {
                TabId = GetInt(reader, 0) ?? 0,
                Name = (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                Owner = (GetString(reader, 2) ?? string.Empty).TrimEnd(),
                Kind = kind,
            },
            cancellationToken,
            parameters.ToArray()).ConfigureAwait(false);

        return new CatalogResult<SchemaObject>(objects, sql);
    }

    private async Task<CatalogResult<SchemaObject>> GetIndexesAsync(
        string? pattern,
        bool includeSystem,
        CancellationToken cancellationToken)
    {
        string sql = CatalogQueries.AllIndexes(includeSystem, pattern);

        object?[] parameters = pattern is null ? [] : [pattern];

        var objects = await QueryAsync(
            sql,
            reader => new SchemaObject
            {
                TabId = GetInt(reader, 0) ?? 0,
                Name = (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                Owner = (GetString(reader, 2) ?? string.Empty).TrimEnd(),
                Kind = SchemaObjectKind.Index,
            },
            cancellationToken,
            parameters).ConfigureAwait(false);

        return new CatalogResult<SchemaObject>(objects, sql);
    }

    private async Task<CatalogResult<SchemaObject>> GetUserDefinedTypesAsync(
        string? pattern,
        bool includeSystem,
        CancellationToken cancellationToken)
    {
        string sql = CatalogQueries.UserDefinedTypes(includeSystem, pattern);

        object?[] parameters = pattern is null ? [] : [pattern];

        var objects = await QueryAsync(
            sql,
            reader => new SchemaObject
            {
                TabId = GetInt(reader, 0) ?? 0,
                Name = (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                Owner = (GetString(reader, 2) ?? string.Empty).TrimEnd(),
                Kind = SchemaObjectKind.UserDefinedType,
            },
            cancellationToken,
            parameters).ConfigureAwait(false);

        return new CatalogResult<SchemaObject>(objects, sql);
    }

    public async Task<CatalogResult<string>> GetOwnersAsync(CancellationToken cancellationToken)
    {
        const string sql = CatalogQueries.Owners;

        var owners = await QueryAsync(
            sql,
            reader => (GetString(reader, 0) ?? string.Empty).TrimEnd(),
            cancellationToken).ConfigureAwait(false);

        return new CatalogResult<string>(owners, sql);
    }

    public async Task<CatalogResult<string>> GetRoutineSourceAsync(
        int procId,
        CancellationToken cancellationToken)
    {
        const string sql = CatalogQueries.RoutineSource;

        var lines = await QueryAsync(
            sql,
            reader => GetString(reader, 0) ?? string.Empty,
            cancellationToken,
            procId).ConfigureAwait(false);

        return new CatalogResult<string>(lines, sql);
    }

    // ---- Table detail (PR-2.4) --------------------------------------------------

    public async Task<TableDetail> GetTableDetailAsync(int tabId, CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Read table detail");

        var queries = new List<string>();

        SchemaObject? header = null;
        string lockMode = "Unknown";
        char rawLockLevel = '?';
        int? firstExtent = null;
        int? nextExtent = null;

        // The header is the one query whose failure means there is nothing to show.
        var rows = await QueryAsync(
            CatalogQueries.TableRow,
            reader => (
                Name: (GetString(reader, 0) ?? string.Empty).TrimEnd(),
                Owner: (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                TabId: GetInt(reader, 2) ?? tabId,
                Rows: GetLong(reader, 3),
                Created: GetDateTime(reader, 4),
                LockLevel: GetString(reader, 5),
                FirstExtent: GetInt(reader, 6),
                NextExtent: GetInt(reader, 7)),
            cancellationToken,
            tabId).ConfigureAwait(false);

        queries.Add(CatalogQueries.TableRow);

        if (rows.Count > 0)
        {
            var row = rows[0];

            rawLockLevel = string.IsNullOrEmpty(row.LockLevel) ? '?' : row.LockLevel[0];
            lockMode = DescribeLockLevel(rawLockLevel);
            firstExtent = row.FirstExtent;
            nextExtent = row.NextExtent;

            header = new SchemaObject
            {
                TabId = row.TabId,
                Name = row.Name,
                Owner = row.Owner,
                Kind = SchemaObjectKind.Table,
                EstimatedRows = row.Rows,
                Created = row.Created,
            };
        }

        header ??= new SchemaObject
        {
            TabId = tabId,
            Name = $"(tabid {tabId})",
            Owner = string.Empty,
            Kind = SchemaObjectKind.Table,
        };

        IReadOnlyList<ColumnDetail> columns = await ReadColumnsAsync(tabId, queries, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<IndexDetail> indexes = await ReadIndexesAsync(
            tabId, columns, queries, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ConstraintDetail> constraints = await ReadConstraintsAsync(
            tabId, indexes, queries, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TriggerDetail> triggers = await ReadTriggersAsync(tabId, queries, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<FragmentDetail> fragments = await ReadFragmentsAsync(tabId, queries, cancellationToken)
            .ConfigureAwait(false);

        (StatisticsCurrency currency, DateTime? updatedAt) =
            await ReadStatisticsAsync(tabId, queries, cancellationToken).ConfigureAwait(false);

        // An index that shares a constraint's name is there to enforce it, not on
        // its own account. Marking them keeps the detail pane from listing the same
        // thing twice under two headings.
        var constraintIndexNames = constraints
            .Select(c => c.IndexName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        indexes = indexes
            .Select(i => constraintIndexNames.Contains(i.Name) ? i with { BacksConstraint = true } : i)
            .ToList();

        return new TableDetail
        {
            Object = header,
            Columns = columns,
            Indexes = indexes,
            Constraints = constraints,
            Triggers = triggers,
            Fragments = fragments,
            LockMode = lockMode,
            RawLockLevel = rawLockLevel,
            FirstExtentKb = firstExtent,
            NextExtentKb = nextExtent,
            DbSpace = fragments.Count == 1 ? fragments[0].DbSpace : null,
            EstimatedRows = header.EstimatedRows,
            Statistics = currency,
            StatisticsUpdatedAt = updatedAt,
            QueriesUsed = queries,
        };
    }

    private async Task<IReadOnlyList<ColumnDetail>> ReadColumnsAsync(
        int tabId,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        queries.Add(CatalogQueries.Columns);

        var raw = await QueryAsync(
            CatalogQueries.Columns,
            reader => (
                ColNo: GetInt(reader, 0) ?? 0,
                Name: (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                ColType: GetInt(reader, 2) ?? 0,
                ColLength: GetInt(reader, 3) ?? 0,
                ExtendedId: GetInt(reader, 4) ?? 0),
            cancellationToken,
            tabId).ConfigureAwait(false);

        // Defaults are isolated: sysdefaults has a column literally named "default",
        // and if the parser refuses it, losing defaults is much cheaper than losing
        // the column list.
        Dictionary<int, (char Type, string? Value)> defaults =
            await ReadDefaultsAsync(tabId, queries, cancellationToken).ConfigureAwait(false);

        // Only fetched when a column actually needs it (PR-6.4).
        Dictionary<int, string> extendedTypes =
            raw.Any(c => InformixTypeMapper.RequiresExtendedTypeLookup(c.ColType))
                ? await ReadExtendedTypesAsync(queries, cancellationToken).ConfigureAwait(false)
                : [];

        var columns = new List<ColumnDetail>(raw.Count);

        foreach (var column in raw)
        {
            InformixDbType dbType = InformixTypeMapper.FromCatalogTypeCode(column.ColType);
            bool nullable = !InformixTypeMapper.IsNotNullFromCatalog(column.ColType);

            // Codes 40 and 41 are "some opaque type"; the name lives in sysxtdtypes.
            string? extendedName = null;

            if (InformixTypeMapper.RequiresExtendedTypeLookup(column.ColType)
                && extendedTypes.TryGetValue(column.ExtendedId, out string? found))
            {
                extendedName = found;
                dbType = InformixTypeMapper.FromServerTypeName(found) is var mapped
                         && mapped != InformixDbType.Other
                    ? mapped
                    : dbType;
            }

            DateTimeQualifier? qualifier = null;

            if (dbType is InformixDbType.DateTime or InformixDbType.Interval)
            {
                try
                {
                    qualifier = InformixTypeMapper.DecodeCatalogQualifier(column.ColLength);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // An encoding IMS does not recognise. Showing the type without a
                    // qualifier is honest; inventing one would not be (PR-8.4).
                }
            }

            (int? precision, int? scale) = DecodeDecimal(dbType, column.ColLength);

            columns.Add(new ColumnDetail
            {
                Position = column.ColNo,
                Name = column.Name,
                DbType = dbType,

                // The server's own name for an opaque type beats anything IMS could
                // infer from the code alone (PR-8.2).
                TypeDescription = extendedName is { Length: > 0 }
                    ? extendedName.ToUpperInvariant()
                    : DescribeType(dbType, column.ColLength, qualifier, precision, scale),
                IsNullable = nullable,
                Qualifier = qualifier,
                Length = dbType is InformixDbType.Char or InformixDbType.VarChar
                                or InformixDbType.NChar or InformixDbType.NVarChar
                    ? column.ColLength & 0xFF
                    : null,
                Precision = precision,
                Scale = scale,
                DefaultValue = defaults.TryGetValue(column.ColNo, out var stored)
                    ? DescribeDefault(stored.Type, stored.Value, dbType)
                    : null,
                RawColType = column.ColType,
                RawColLength = column.ColLength,
            });
        }

        return columns;
    }

    private async Task<Dictionary<int, (char Type, string? Value)>> ReadDefaultsAsync(
        int tabId,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.Defaults,
                reader => (
                    ColNo: GetInt(reader, 0) ?? 0,
                    Type: GetString(reader, 1),
                    Value: GetString(reader, 2)),
                cancellationToken,
                tabId).ConfigureAwait(false);

            queries.Add(CatalogQueries.Defaults);

            return rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Type))
                .ToDictionary(r => r.ColNo, r => (r.Type![0], r.Value));
        }
        catch (OdbcException ex)
        {
            _logger.LogInformation(
                "Column defaults are unavailable on this server: {Message}", ex.Message);

            return [];
        }
    }

    /// <summary>
    /// Extended type names by <c>extended_id</c>, for catalogue codes 40 and 41.
    /// </summary>
    private async Task<Dictionary<int, string>> ReadExtendedTypesAsync(
        List<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.ExtendedTypes,
                reader => (
                    Id: GetInt(reader, 0) ?? 0,
                    Name: (GetString(reader, 1) ?? string.Empty).TrimEnd()),
                cancellationToken).ConfigureAwait(false);

            queries.Add(CatalogQueries.ExtendedTypes);

            var map = new Dictionary<int, string>();

            foreach (var row in rows)
            {
                map[row.Id] = row.Name;
            }

            return map;
        }
        catch (OdbcException ex)
        {
            // Falling back to "OTHER" is honest; guessing between BLOB, CLOB,
            // BOOLEAN and a user-defined type would not be (PR-8.4).
            _logger.LogInformation(
                "sysxtdtypes is unavailable, so opaque column types stay unresolved: {Message}",
                ex.Message);

            return [];
        }
    }

    private async Task<IReadOnlyList<IndexDetail>> ReadIndexesAsync(
        int tabId,
        IReadOnlyList<ColumnDetail> columns,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.Indexes,
                reader =>
                {
                    var parts = new List<int>();

                    for (int i = 5; i <= 20; i++)
                    {
                        int part = GetInt(reader, i) ?? 0;

                        if (part != 0)
                        {
                            parts.Add(part);
                        }
                    }

                    return (
                        Name: (GetString(reader, 0) ?? string.Empty).TrimEnd(),
                        Owner: (GetString(reader, 1) ?? string.Empty).TrimEnd(),

                        // Trimmed: these are CHAR(1) columns and come back padded,
                        // so an untrimmed comparison quietly reported every index as
                        // non-unique.
                        IdxType: (GetString(reader, 2) ?? string.Empty).Trim(),
                        Clustered: (GetString(reader, 3) ?? string.Empty).Trim(),
                        Levels: GetInt(reader, 4),
                        Parts: parts);
                },
                cancellationToken,
                tabId).ConfigureAwait(false);

            queries.Add(CatalogQueries.Indexes);

            return rows.Select(r => new IndexDetail
            {
                Name = r.Name,
                Owner = r.Owner,

                // idxtype 'U' is unique, 'D' allows duplicates.
                IsUnique = string.Equals(r.IdxType, "U", StringComparison.OrdinalIgnoreCase),
                IsClustered = string.Equals(r.Clustered, "C", StringComparison.OrdinalIgnoreCase),
                Levels = r.Levels,
                Columns = r.Parts.Select(p => NameForPart(p, columns)).ToList(),
            }).ToList();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning("Indexes could not be read: {Message}", ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<ConstraintDetail>> ReadConstraintsAsync(
        int tabId,
        IReadOnlyList<IndexDetail> indexes,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.Constraints,
                reader => (
                    ConstrId: GetInt(reader, 0) ?? 0,
                    Name: (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                    Owner: (GetString(reader, 2) ?? string.Empty).TrimEnd(),
                    Type: GetString(reader, 3),
                    IdxName: (GetString(reader, 4) ?? string.Empty).TrimEnd()),
                cancellationToken,
                tabId).ConfigureAwait(false);

            queries.Add(CatalogQueries.Constraints);

            var constraints = new List<ConstraintDetail>(rows.Count);

            foreach (var row in rows)
            {
                char rawType = string.IsNullOrEmpty(row.Type) ? '?' : row.Type[0];
                ConstraintKind kind = DescribeConstraint(rawType);

                // Prefer the backing index's key: it gives the columns in key order,
                // which syscoldepend does not.
                IReadOnlyList<string> columns =
                    indexes.FirstOrDefault(i =>
                        string.Equals(i.Name, row.IdxName, StringComparison.OrdinalIgnoreCase))?.Columns
                    ?? await ReadConstraintColumnsAsync(row.ConstrId, cancellationToken).ConfigureAwait(false);

                string? checkText = kind == ConstraintKind.Check
                    ? await ReadCheckTextAsync(row.ConstrId, cancellationToken).ConfigureAwait(false)
                    : null;

                (string? refTable, _) = kind == ConstraintKind.ForeignKey
                    ? await ReadForeignKeyTargetAsync(row.ConstrId, cancellationToken).ConfigureAwait(false)
                    : (null, null);

                constraints.Add(new ConstraintDetail
                {
                    Name = row.Name,
                    Owner = row.Owner,
                    Kind = kind,
                    Columns = columns,
                    IndexName = string.IsNullOrWhiteSpace(row.IdxName) ? null : row.IdxName,
                    CheckExpression = checkText,
                    ReferencedTable = refTable,
                    RawConstraintType = rawType,
                });
            }

            return constraints;
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning("Constraints could not be read: {Message}", ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ReadConstraintColumnsAsync(
        int constrId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await QueryAsync(
                CatalogQueries.ConstraintColumns,
                reader => (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                cancellationToken,
                constrId).ConfigureAwait(false);
        }
        catch (OdbcException)
        {
            return [];
        }
    }

    private async Task<string?> ReadCheckTextAsync(int constrId, CancellationToken cancellationToken)
    {
        try
        {
            var fragments = await QueryAsync(
                CatalogQueries.CheckText,
                reader => GetString(reader, 0) ?? string.Empty,
                cancellationToken,
                constrId).ConfigureAwait(false);

            // syschecks stores the text in numbered fragments; joining them back is
            // the caller's job, and the seqno ordering in the query guarantees it.
            string text = string.Concat(fragments).Trim();

            return text.Length == 0 ? null : text;
        }
        catch (OdbcException)
        {
            return null;
        }
    }

    private async Task<(string? Table, string? Constraint)> ReadForeignKeyTargetAsync(
        int constrId,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.ForeignKeyTarget,
                reader => (
                    Table: (GetString(reader, 0) ?? string.Empty).TrimEnd(),
                    Owner: (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                    Constraint: (GetString(reader, 2) ?? string.Empty).TrimEnd()),
                cancellationToken,
                constrId).ConfigureAwait(false);

            return rows.Count == 0 ? (null, null) : (rows[0].Table, rows[0].Constraint);
        }
        catch (OdbcException)
        {
            return (null, null);
        }
    }

    private async Task<IReadOnlyList<TriggerDetail>> ReadTriggersAsync(
        int tabId,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.Triggers,
                reader => (
                    Name: (GetString(reader, 0) ?? string.Empty).TrimEnd(),
                    Owner: (GetString(reader, 1) ?? string.Empty).TrimEnd(),
                    Event: GetString(reader, 2)),
                cancellationToken,
                tabId).ConfigureAwait(false);

            queries.Add(CatalogQueries.Triggers);

            return rows.Select(r =>
            {
                char rawEvent = string.IsNullOrEmpty(r.Event) ? '?' : r.Event[0];

                return new TriggerDetail
                {
                    Name = r.Name,
                    Owner = r.Owner,
                    Event = DescribeTriggerEvent(rawEvent),
                    RawEvent = rawEvent,
                };
            }).ToList();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning("Triggers could not be read: {Message}", ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<FragmentDetail>> ReadFragmentsAsync(
        int tabId,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(
                CatalogQueries.Fragments,
                reader => (
                    Strategy: GetString(reader, 1),
                    EvalPos: GetInt(reader, 2),
                    Expression: GetString(reader, 3),
                    DbSpace: (GetString(reader, 4) ?? string.Empty).TrimEnd()),
                cancellationToken,
                tabId).ConfigureAwait(false);

            queries.Add(CatalogQueries.Fragments);

            return rows.Select(r =>
            {
                char rawStrategy = string.IsNullOrEmpty(r.Strategy) ? '?' : r.Strategy[0];

                return new FragmentDetail
                {
                    Strategy = DescribeFragmentStrategy(rawStrategy),
                    RawStrategy = rawStrategy,
                    DbSpace = r.DbSpace,
                    Expression = string.IsNullOrWhiteSpace(r.Expression) ? null : r.Expression.Trim(),
                    Position = r.EvalPos,
                };
            }).ToList();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning("Fragmentation could not be read: {Message}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Statistics currency (PR-2.5), with the capability probed rather than assumed.
    /// </summary>
    /// <remarks>
    /// NFR-4 asks for capability detection instead of version branching, so IMS
    /// tries <c>ustlowts</c> once and remembers whether it worked. Where it is
    /// absent the answer is <see cref="StatisticsCurrency.Unknown"/> — which is the
    /// truth, and better than a confident wrong answer.
    /// </remarks>
    private async Task<(StatisticsCurrency, DateTime?)> ReadStatisticsAsync(
        int tabId,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        if (_hasStatisticsTimestamp == false)
        {
            return (StatisticsCurrency.Unknown, null);
        }

        try
        {
            var rows = await QueryAsync(
                CatalogQueries.StatisticsTimestamp,
                reader => GetDateTime(reader, 0),
                cancellationToken,
                tabId).ConfigureAwait(false);

            _hasStatisticsTimestamp = true;
            queries.Add(CatalogQueries.StatisticsTimestamp);

            DateTime? updated = rows.Count > 0 ? rows[0] : null;

            if (updated is null)
            {
                return (StatisticsCurrency.Never, null);
            }

            // Thirty days is a judgement, not a rule Informix enforces. It is here to
            // prompt a look rather than to be authoritative, and the timestamp is
            // always shown alongside so the user can judge for themselves.
            StatisticsCurrency currency = DateTime.Now - updated.Value > TimeSpan.FromDays(30)
                ? StatisticsCurrency.Stale
                : StatisticsCurrency.Current;

            return (currency, updated);
        }
        catch (OdbcException ex)
        {
            _hasStatisticsTimestamp = false;

            _logger.LogInformation(
                "This server does not expose systables.ustlowts, so statistics currency "
                + "cannot be reported: {Message}",
                ex.Message);

            return (StatisticsCurrency.Unknown, null);
        }
    }

    // ---- Plumbing ---------------------------------------------------------------

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<OdbcDataReader, T> map,
        CancellationToken cancellationToken,
        params object?[] parameters)
    {
        ServerCallGuard.AssertNotOnUiThread("Run a catalogue query");

        OdbcConnection connection = _connection
            ?? throw new InvalidOperationException("The catalogue reader is not open.");

        using var command = new OdbcCommand(sql, connection) { CommandTimeout = 60 };

        foreach (object? parameter in parameters)
        {
            command.Parameters.AddWithValue(string.Empty, parameter ?? DBNull.Value);
        }

        using OdbcDataReader reader = (OdbcDataReader)await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<T>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(map(reader));
        }

        return items;
    }

    private static string? GetString(OdbcDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();

    private static int? GetInt(OdbcDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? GetLong(OdbcDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateTime? GetDateTime(OdbcDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDateTime(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool GetBoolean(OdbcDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        object value = reader.GetValue(ordinal);

        return value switch
        {
            bool flag => flag,
            string text => text.Length > 0 && (text[0] is 't' or 'T' or '1' or 'y' or 'Y'),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0,
        };
    }

    // ---- Catalogue code translation ---------------------------------------------

    internal static string TabTypeFor(SchemaObjectKind kind) => kind switch
    {
        SchemaObjectKind.Table => "T",
        SchemaObjectKind.View => "V",
        SchemaObjectKind.Synonym => "S",
        SchemaObjectKind.PrivateSynonym => "P",
        SchemaObjectKind.Sequence => "Q",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a systables type."),
    };

    internal static string DescribeLockLevel(char locklevel) => locklevel switch
    {
        'B' or 'P' => "Page",
        'R' => "Row",
        'T' => "Table",
        _ => $"Unknown ('{locklevel}')",
    };

    internal static ConstraintKind DescribeConstraint(char constrtype) => constrtype switch
    {
        'P' => ConstraintKind.PrimaryKey,
        'U' => ConstraintKind.Unique,
        'R' => ConstraintKind.ForeignKey,
        'C' => ConstraintKind.Check,
        'N' => ConstraintKind.NotNull,
        _ => ConstraintKind.Other,
    };

    internal static string DescribeTriggerEvent(char trigEvent) => trigEvent switch
    {
        'I' => "INSERT",
        'U' => "UPDATE",
        'D' => "DELETE",
        'S' => "SELECT",
        _ => $"Unknown ('{trigEvent}')",
    };

    internal static string DescribeFragmentStrategy(char strategy) => strategy switch
    {
        'R' => "Round robin",
        'E' => "Expression",
        'H' => "Hash",
        'I' => "Interval",
        'L' => "List",
        'T' => "Table",
        _ => $"Unknown ('{strategy}')",
    };

    internal static DatabaseLogging DescribeLogging(bool isLogging, bool isBuffered, bool isAnsi) =>
        (isLogging, isBuffered, isAnsi) switch
        {
            (_, _, true) => DatabaseLogging.Ansi,
            (true, true, _) => DatabaseLogging.Buffered,
            (true, false, _) => DatabaseLogging.Unbuffered,
            (false, _, _) => DatabaseLogging.None,
        };

    /// <summary>
    /// Turns a <c>sysdefaults</c> row into the default a user would write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The literal case is not simply the stored text. For any column that is not a
    /// character type, Informix stores a literal default as an encoded prefix, a
    /// space, then the literal — an INTEGER defaulting to 0 is stored as
    /// <c>"AAAAAA 0"</c>. Showing the raw value put that encoding in front of the
    /// user, which is the opposite of what PR-2.4 is for.
    /// </para>
    /// <para>
    /// Character defaults are left exactly as stored, because there the whole value
    /// is the default and it may legitimately contain spaces.
    /// </para>
    /// </remarks>
    internal static string DescribeDefault(char type, string? value, InformixDbType dbType) => type switch
    {
        'C' => "CURRENT",
        'N' => "NULL",
        'T' => "TODAY",
        'U' => "USER",
        'S' => "DBSERVERNAME",
        'L' => StripDefaultEncoding(value, dbType),
        _ => value?.Trim() ?? $"({type})",
    };

    internal static string StripDefaultEncoding(string? value, InformixDbType dbType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (IsCharacterType(dbType))
        {
            return value.Trim();
        }

        // Everything else carries the encoded prefix. Split once: the literal itself
        // may contain spaces (a DATETIME default, for instance).
        int space = value.IndexOf(' ', StringComparison.Ordinal);

        return space >= 0 ? value[(space + 1)..].Trim() : value.Trim();
    }

    private static bool IsCharacterType(InformixDbType dbType) =>
        dbType is InformixDbType.Char or InformixDbType.VarChar or InformixDbType.NChar
               or InformixDbType.NVarChar or InformixDbType.LVarChar;

    /// <summary>
    /// Precision and scale for DECIMAL and MONEY, which the catalogue packs into
    /// <c>collength</c> as <c>(precision * 256) + scale</c>.
    /// </summary>
    internal static (int? Precision, int? Scale) DecodeDecimal(InformixDbType dbType, int collength) =>
        dbType is InformixDbType.Decimal or InformixDbType.Money
            ? (collength / 256, collength % 256)
            : (null, null);

    internal static string DescribeType(
        InformixDbType dbType,
        int collength,
        DateTimeQualifier? qualifier,
        int? precision,
        int? scale) => dbType switch
        {
            InformixDbType.DateTime when qualifier is { } q => $"DATETIME {q}",
            InformixDbType.Interval when qualifier is { } q => $"INTERVAL {q}",
            InformixDbType.Decimal when precision is { } p => $"DECIMAL({p},{scale ?? 0})",
            InformixDbType.Money when precision is { } p => $"MONEY({p},{scale ?? 0})",
            InformixDbType.Char => $"CHAR({collength})",
            InformixDbType.VarChar => $"VARCHAR({collength & 0xFF})",
            InformixDbType.NChar => $"NCHAR({collength})",
            InformixDbType.NVarChar => $"NVARCHAR({collength & 0xFF})",
            InformixDbType.LVarChar => $"LVARCHAR({collength})",
            _ => dbType.ToString().ToUpperInvariant(),
        };

    private static string NameForPart(int part, IReadOnlyList<ColumnDetail> columns)
    {
        // A negative part number means the column is indexed in descending order.
        bool descending = part < 0;
        int colno = Math.Abs(part);

        string name = columns.FirstOrDefault(c => c.Position == colno)?.Name
                      ?? $"(column {colno})";

        return descending ? name + " DESC" : name;
    }
}
