namespace Ims.Data.Informix.Catalog;

/// <summary>
/// The catalogue queries IMS runs, as text.
/// </summary>
/// <remarks>
/// <para>
/// Kept in one place and as constants for three reasons. PR-8.2 requires the
/// underlying query to be available on demand, and a query the user can be shown
/// should be a query a developer can read. PR-6.4 requires them to stay light
/// enough to be negligible on a production instance, which is easier to hold
/// yourself to when they are all visible together. And PR-8.3 says IMS should teach
/// the platform — someone who reads these learns the Informix catalogue.
/// </para>
/// <para>
/// <c>tabid &gt; 99</c> is Informix's own boundary: the system catalogue occupies
/// tabids 1-99, so everything above it is a user object.
/// </para>
/// </remarks>
internal static class CatalogQueries
{
    /// <summary>Databases on the instance. Needs sysmaster, confirmed readable (Q-1).</summary>
    public const string Databases = """
        SELECT name, owner, is_logging, is_buff_log, is_ansi
          FROM sysmaster:sysdatabases
         ORDER BY name
        """;

    /// <summary>
    /// Objects from systables, by tabtype.
    /// </summary>
    /// <remarks>
    /// tabtype: T table, V view, S public synonym, P private synonym, Q sequence.
    /// <para>
    /// The system-object predicate is composed here rather than passed as a
    /// parameter. A bare <c>? = 1</c> gives Informix no type to infer, and composing
    /// it also means the SQL shown to the user under PR-8.2 is exactly the SQL that
    /// ran, rather than a template they have to interpret.
    /// </para>
    /// </remarks>
    public static string ObjectsByType(bool includeSystem, string? nameFilter, string? owner)
    {
        var sql = new System.Text.StringBuilder("""
            SELECT tabid, tabname, owner, nrows, created
              FROM systables
             WHERE tabtype = ?
            """);

        if (!includeSystem)
        {
            sql.AppendLine().Append("   AND tabid > 99");
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            sql.AppendLine().Append("   AND LOWER(tabname) LIKE ?");
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            sql.AppendLine().Append("   AND owner = ?");
        }

        return sql.AppendLine().Append(" ORDER BY owner, tabname").ToString();
    }

    /// <summary>Procedures and functions. isproc 't' is a procedure, 'f' a function.</summary>
    public static string Routines(bool includeSystem, string? nameFilter, string? owner)
    {
        var sql = new System.Text.StringBuilder("""
            SELECT procid, procname, owner, isproc
              FROM sysprocedures
             WHERE isproc = ?
            """);

        if (!includeSystem)
        {
            // System routines ship owned by informix. Not a perfect boundary, but
            // the honest one available from the catalogue alone.
            sql.AppendLine().Append("   AND owner <> 'informix'");
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            sql.AppendLine().Append("   AND LOWER(procname) LIKE ?");
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            sql.AppendLine().Append("   AND owner = ?");
        }

        return sql.AppendLine().Append(" ORDER BY owner, procname").ToString();
    }

    /// <summary>
    /// User-defined types.
    /// </summary>
    /// <remarks>
    /// This was the one listing query that failed against 14.10, and the cause was
    /// never diagnosed — the object tree's Types folder is descoped as a result. The
    /// two-column <see cref="ExtendedTypes"/> form used by the detail pane works
    /// fine, so the table is readable and the fault is in this query's shape.
    /// <para>
    /// The likely culprit was a placeholder <c>WHERE 1 = 1</c> that the predicates
    /// were appended to; it is gone, and the clause is now built only when there is
    /// something to put in it. Untested — if the folder is ever revived, this is the
    /// first thing to try.
    /// </para>
    /// </remarks>
    public static string UserDefinedTypes(bool includeSystem, string? nameFilter)
    {
        var predicates = new List<string>();

        if (!includeSystem)
        {
            predicates.Add("owner <> 'informix'");
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            predicates.Add("LOWER(name) LIKE ?");
        }

        var sql = new System.Text.StringBuilder("""
            SELECT extended_id, name, owner
              FROM sysxtdtypes
            """);

        if (predicates.Count > 0)
        {
            sql.AppendLine().Append(" WHERE ").Append(string.Join(Environment.NewLine + "   AND ", predicates));
        }

        return sql.AppendLine().Append(" ORDER BY owner, name").ToString();
    }

    /// <summary>Every index in the database, for the tree's Indexes node.</summary>
    public static string AllIndexes(bool includeSystem, string? nameFilter)
    {
        var sql = new System.Text.StringBuilder("""
            SELECT i.tabid, i.idxname, i.owner, t.tabname
              FROM sysindexes i, systables t
             WHERE i.tabid = t.tabid
            """);

        if (!includeSystem)
        {
            sql.AppendLine().Append("   AND i.tabid > 99");
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            sql.AppendLine().Append("   AND LOWER(i.idxname) LIKE ?");
        }

        return sql.AppendLine().Append(" ORDER BY i.owner, i.idxname").ToString();
    }

    /// <summary>Owners that actually own something (PR-2.3).</summary>
    public const string Owners = """
        SELECT DISTINCT owner
          FROM systables
         WHERE tabid > 99
         ORDER BY owner
        """;

    // ---- One table's detail (PR-2.4) -------------------------------------------

    /// <summary>
    /// The table row itself.
    /// </summary>
    /// <remarks>
    /// <c>ustlowts</c> is the UPDATE STATISTICS LOW timestamp and drives PR-2.5. It
    /// does not exist on every version, so it is fetched separately rather than
    /// letting its absence cost the whole row — NFR-4's degrade-gracefully rule.
    /// </remarks>
    public const string TableRow = """
        SELECT tabname, owner, tabid, nrows, created, locklevel,
               fextsize, nextsize, npused, partnum
          FROM systables
         WHERE tabid = ?
        """;

    /// <summary>Statistics currency (PR-2.5). Probed separately; may not exist.</summary>
    public const string StatisticsTimestamp = """
        SELECT ustlowts
          FROM systables
         WHERE tabid = ?
        """;

    public const string Columns = """
        SELECT colno, colname, coltype, collength, extended_id
          FROM syscolumns
         WHERE tabid = ?
         ORDER BY colno
        """;

    /// <summary>
    /// Column defaults.
    /// </summary>
    /// <remarks>
    /// Separate because <c>sysdefaults</c> has a column literally named
    /// <c>default</c>, and whether the parser accepts it unquoted is exactly the
    /// kind of thing that varies. Isolated so that if it fails, IMS loses defaults
    /// rather than the whole column list.
    /// </remarks>
    public const string Defaults = """
        SELECT colno, type, default
          FROM sysdefaults
         WHERE tabid = ?
        """;

    /// <summary>
    /// Indexes on a table.
    /// </summary>
    /// <remarks>
    /// <c>sysindexes</c> rather than <c>sysindices</c>: the former is the
    /// compatibility view that exposes part1..part16 as plain columns, where the
    /// latter stores the key as a composite type. A negative part is a descending
    /// column.
    /// </remarks>
    public const string Indexes = """
        SELECT idxname, owner, idxtype, clustered, levels,
               part1, part2, part3, part4, part5, part6, part7, part8,
               part9, part10, part11, part12, part13, part14, part15, part16
          FROM sysindexes
         WHERE tabid = ?
         ORDER BY idxname
        """;

    /// <summary>constrtype: P primary, U unique, R referential, C check, N not null.</summary>
    public const string Constraints = """
        SELECT constrid, constrname, owner, constrtype, idxname
          FROM sysconstraints
         WHERE tabid = ?
         ORDER BY constrtype, constrname
        """;

    /// <summary>The columns a constraint covers.</summary>
    public const string ConstraintColumns = """
        SELECT d.colno, c.colname
          FROM syscoldepend d, syscolumns c
         WHERE d.constrid = ?
           AND c.tabid = d.tabid
           AND c.colno = d.colno
         ORDER BY d.colno
        """;

    /// <summary>A check constraint's text, stored in numbered fragments.</summary>
    public const string CheckText = """
        SELECT checktext
          FROM syschecks
         WHERE constrid = ?
           AND type = 'T'
         ORDER BY seqno
        """;

    /// <summary>The table a foreign key points at.</summary>
    public const string ForeignKeyTarget = """
        SELECT t.tabname, t.owner, c.constrname
          FROM sysreferences r, sysconstraints c, systables t
         WHERE r.constrid = ?
           AND c.constrid = r.primary
           AND t.tabid = c.tabid
        """;

    /// <summary>event: I insert, U update, D delete, S select.</summary>
    public const string Triggers = """
        SELECT trigname, owner, event
          FROM systriggers
         WHERE tabid = ?
         ORDER BY trigname
        """;

    /// <summary>
    /// Fragmentation (PR-2.4).
    /// </summary>
    /// <remarks>
    /// strategy: R round robin, E expression, H hash, I interval, L list.
    /// A table with a single row here is not fragmented; the row records its dbspace.
    /// </remarks>
    public const string Fragments = """
        SELECT partn, strategy, evalpos, exprtext, dbspace
          FROM sysfragments
         WHERE tabid = ?
           AND fragtype = 'T'
         ORDER BY evalpos
        """;

    /// <summary>
    /// Extended type names, for the opaque catalogue codes 40 and 41.
    /// </summary>
    /// <remarks>
    /// <c>syscolumns.coltype</c> 40 and 41 cover BLOB, CLOB, BOOLEAN, LVARCHAR and
    /// every user-defined type without distinguishing them; the real type is found
    /// by joining <c>extended_id</c> to here. Only two columns are selected because
    /// the wider <c>sysxtdtypes</c> query used by the object tree failed against
    /// 14.10 and the cause is not yet known — asking for less is more likely to work.
    /// </remarks>
    public const string ExtendedTypes = """
        SELECT extended_id, name
          FROM sysxtdtypes
        """;

    /// <summary>A routine's source, stored as numbered lines.</summary>
    public const string RoutineSource = """
        SELECT data
          FROM sysprocbody
         WHERE procid = ?
           AND datakey = 'T'
         ORDER BY seqno
        """;
}
