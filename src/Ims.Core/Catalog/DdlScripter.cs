using System.Globalization;
using System.Text;

namespace Ims.Core.Catalog;

/// <summary>
/// Turns catalogue metadata back into DDL (PR-2.6).
/// </summary>
/// <remarks>
/// <para>
/// The target is <c>dbschema</c>: same content, same shape, so that a developer who
/// knows what <c>dbschema -d db -t table</c> produces reads this without adjusting,
/// and so that the two can be diffed. That is why the output is lower case and why
/// the clause order matches — column list, table constraints, fragmentation, extent
/// sizing, lock mode — rather than following any house style.
/// </para>
/// <para>
/// <b>Known differences from <c>dbschema</c>, all deliberate:</b>
/// </para>
/// <list type="bullet">
///   <item>
///     Two leading comment lines naming the source. A script that arrives in an
///     editor tab with no provenance is a script someone will mistake for the
///     server's own words.
///   </item>
///   <item>
///     No <c>{ TABLE ... row size = n }</c> banner. It reports storage arithmetic IMS
///     does not compute, and PR-8.4 rules out presenting an inference as a fact.
///   </item>
///   <item>
///     No <c>grant</c> or <c>revoke</c>. IMS does not read <c>systabauth</c>, so the
///     script is not a complete transfer of a table — and it says so in the comment
///     rather than leaving the omission to be discovered.
///   </item>
///   <item>
///     Triggers are listed in a trailing comment rather than scripted. Their text
///     lives in <c>systrigbody</c>, which IMS does not read yet.
///   </item>
/// </list>
/// <para>
/// This class is pure: metadata in, text out, no server. That is what makes the
/// PR-2.6 acceptance test — diff against real <c>dbschema</c> output — something that
/// can be run against a golden file rather than only against a live instance.
/// </para>
/// </remarks>
public static class DdlScripter
{
    private const string Indent = "    ";

    /// <summary>Scripts a table as <c>CREATE TABLE</c> plus its standalone indexes.</summary>
    public static string ScriptTable(TableDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var sql = new StringBuilder();

        AppendProvenance(sql, detail.Object);

        sql.Append("create table ").Append(Qualify(detail.Object.Owner, detail.Object.Name))
           .AppendLine().AppendLine("  (");

        var clauses = new List<string>();

        foreach (ColumnDetail column in detail.Columns)
        {
            clauses.Add(ColumnClause(column));
        }

        foreach (ConstraintDetail constraint in detail.Constraints)
        {
            // A NOT NULL constraint is already on its column. Informix records one in
            // sysconstraints as well, and emitting it here would produce a table-level
            // clause no version of Informix accepts.
            if (constraint.Kind == ConstraintKind.NotNull)
            {
                continue;
            }

            if (ConstraintClause(constraint) is { } clause)
            {
                clauses.Add(clause);
            }
        }

        for (var i = 0; i < clauses.Count; i++)
        {
            sql.Append(Indent).Append(clauses[i])
               .AppendLine(i == clauses.Count - 1 ? string.Empty : ",");
        }

        sql.Append("  )").Append(StorageClause(detail)).AppendLine(";");

        AppendIndexes(sql, detail);
        AppendTriggerNote(sql, detail);

        return sql.ToString();
    }

    /// <summary>
    /// Scripts one index on its own, for the tree's Indexes folder.
    /// </summary>
    public static string ScriptIndex(SchemaObject table, IndexDetail index)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        var sql = new StringBuilder();

        AppendProvenance(sql, table, $"index {index.Name} on {table.Name}");

        if (index.BacksConstraint)
        {
            // Re-running this would create a second index. The constraint that owns it
            // is the thing to script, so say so instead of handing over a statement
            // that is wrong in a way the user would only find out by running it.
            sql.Append("-- This index exists to enforce a constraint; script the table instead.")
               .AppendLine().AppendLine();
        }

        sql.Append(IndexStatement(table, index));

        return sql.ToString();
    }

    /// <summary>
    /// Scripts a view from the text the server holds.
    /// </summary>
    /// <remarks>
    /// <c>sysviews.viewtext</c> already contains a complete <c>create view</c>
    /// statement, split across numbered rows. Reassembling it and leaving it alone
    /// is both less work and more faithful than rebuilding the statement from the
    /// column list — PR-8.2's principle, applied to DDL.
    /// </remarks>
    public static string ScriptView(SchemaObject view, IReadOnlyList<string> viewText)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(viewText);

        var sql = new StringBuilder();

        AppendProvenance(sql, view);

        string text = string.Concat(viewText).Trim();

        if (text.Length == 0)
        {
            return sql.Append("-- The server returned no text for this view.").AppendLine().ToString();
        }

        sql.Append(text);

        if (!text.EndsWith(';'))
        {
            sql.Append(';');
        }

        return sql.AppendLine().ToString();
    }

    /// <summary>
    /// Scripts a procedure or function from <c>sysprocbody</c>.
    /// </summary>
    /// <remarks>
    /// The stored text is the routine's own source, newlines and comments included.
    /// It is returned verbatim; reformatting it would lose the author's layout for no
    /// gain, and would make a diff against the server useless.
    /// </remarks>
    public static string ScriptRoutine(SchemaObject routine, IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(source);

        var sql = new StringBuilder();

        AppendProvenance(sql, routine);

        string text = string.Concat(source).TrimEnd();

        if (text.Length == 0)
        {
            return sql
                .Append("-- The server returned no text. A routine written in a language other")
                .AppendLine()
                .Append("-- than SPL keeps its body outside sysprocbody.")
                .AppendLine()
                .ToString();
        }

        sql.Append(text);

        if (!text.TrimEnd().EndsWith(';'))
        {
            sql.Append(';');
        }

        return sql.AppendLine().ToString();
    }

    // ---- Table clauses -----------------------------------------------------------

    internal static string ColumnClause(ColumnDetail column)
    {
        var clause = new StringBuilder()
            .Append(Identifier(column.Name))
            .Append(' ')
            .Append(column.TypeDescription.ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(column.DefaultValue))
        {
            clause.Append(" default ").Append(DefaultLiteral(column));
        }

        if (!column.IsNullable && !IsImplicitlyNotNull(column))
        {
            clause.Append(" not null");
        }

        return clause.ToString();
    }

    /// <summary>
    /// Renders a default the way it has to be written back.
    /// </summary>
    /// <remarks>
    /// The catalogue stores a character default as its bare text, so scripting it
    /// unquoted would produce SQL that either fails or — worse, for a value that
    /// happens to parse as an identifier — silently means something else. The
    /// keyword defaults (CURRENT, TODAY, USER, NULL, DBSERVERNAME) are keywords and
    /// must not be quoted.
    /// </remarks>
    internal static string DefaultLiteral(ColumnDetail column)
    {
        string value = column.DefaultValue!.Trim();

        if (IsDefaultKeyword(value))
        {
            return value.ToLowerInvariant();
        }

        return NeedsQuoting(column.DbType)
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : value;
    }

    private static bool IsDefaultKeyword(string value) =>
        value.Equals("CURRENT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("TODAY", StringComparison.OrdinalIgnoreCase)
        || value.Equals("USER", StringComparison.OrdinalIgnoreCase)
        || value.Equals("NULL", StringComparison.OrdinalIgnoreCase)
        || value.Equals("DBSERVERNAME", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("CURRENT ", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsQuoting(Data.InformixDbType dbType) =>
        dbType is Data.InformixDbType.Char or Data.InformixDbType.VarChar
               or Data.InformixDbType.NChar or Data.InformixDbType.NVarChar
               or Data.InformixDbType.LVarChar or Data.InformixDbType.DateTime
               or Data.InformixDbType.Date or Data.InformixDbType.Interval;

    /// <summary>
    /// True for a type whose NOT NULL is part of the type name.
    /// </summary>
    /// <remarks>
    /// SERIAL, SERIAL8 and BIGSERIAL are never nullable, and Informix rejects
    /// <c>serial not null</c> on some paths. dbschema emits the words anyway, so this
    /// is one place the two intentionally differ in favour of a script that runs.
    /// </remarks>
    private static bool IsImplicitlyNotNull(ColumnDetail column) =>
        column.DbType is Data.InformixDbType.Serial or Data.InformixDbType.Serial8
                      or Data.InformixDbType.BigSerial;

    internal static string? ConstraintClause(ConstraintDetail constraint)
    {
        string named = " constraint " + Qualify(constraint.Owner, constraint.Name);
        string columns = string.Join(",", constraint.Columns.Select(Identifier));

        return constraint.Kind switch
        {
            ConstraintKind.PrimaryKey => $"primary key ({columns}){named}",
            ConstraintKind.Unique => $"unique ({columns}){named}",

            ConstraintKind.ForeignKey when constraint.ReferencedTable is { Length: > 0 } target =>
                $"foreign key ({columns}) references "
                + Qualify(constraint.ReferencedOwner ?? string.Empty, target)
                + named,

            ConstraintKind.Check when constraint.CheckExpression is { Length: > 0 } expression =>
                $"check ({expression}){named}",

            // A foreign key whose target could not be read, or a check whose text the
            // server would not give up. Emitting a half statement would produce SQL
            // that fails; naming the gap lets the user go and look.
            ConstraintKind.ForeignKey or ConstraintKind.Check =>
                $"-- {constraint.Kind} constraint {constraint.Name} could not be scripted: "
                + "the catalogue did not return its definition",

            _ => null,
        };
    }

    /// <summary>Everything after the closing bracket: fragmentation, extents, lock mode.</summary>
    internal static string StorageClause(TableDetail detail)
    {
        var clause = new StringBuilder();

        if (detail.IsFragmented)
        {
            clause.Append(FragmentClause(detail.Fragments));
        }
        else if (detail.DbSpace is { Length: > 0 } dbspace)
        {
            clause.Append(" in ").Append(Identifier(dbspace));
        }

        if (detail.FirstExtentKb is { } first)
        {
            clause.Append(" extent size ").Append(first.ToString(CultureInfo.InvariantCulture));
        }

        if (detail.NextExtentKb is { } next)
        {
            clause.Append(" next size ").Append(next.ToString(CultureInfo.InvariantCulture));
        }

        if (LockModeWord(detail.LockMode) is { } lockMode)
        {
            clause.Append(" lock mode ").Append(lockMode);
        }

        return clause.ToString();
    }

    private static string FragmentClause(IReadOnlyList<FragmentDetail> fragments)
    {
        char strategy = fragments[0].RawStrategy;

        var clause = new StringBuilder().AppendLine().Append("  fragment by ");

        switch (strategy)
        {
            case 'R':
                clause.Append("round robin in ")
                      .Append(string.Join(", ", fragments.Select(f => Identifier(f.DbSpace))));
                break;

            case 'E' or 'I' or 'L' or 'H':
                clause.Append(strategy switch
                {
                    'E' => "expression",
                    'I' => "interval",
                    'L' => "list",
                    _ => "hash",
                });

                foreach (FragmentDetail fragment in fragments)
                {
                    clause.AppendLine().Append(Indent);

                    // The last fragment of an expression scheme is the remainder, and
                    // the catalogue records it with no expression.
                    clause.Append(fragment.Expression is { Length: > 0 } expression
                        ? "(" + expression + ") in "
                        : "remainder in ");

                    clause.Append(Identifier(fragment.DbSpace)).Append(',');
                }

                clause.Length -= 1;
                break;

            default:
                // NFR-4: an unknown strategy is reported, not guessed at. The script is
                // still useful; the user is told exactly what is missing from it.
                clause.Clear()
                      .AppendLine()
                      .Append("  -- fragmented by an unrecognised strategy '")
                      .Append(strategy)
                      .Append("'; the fragmentation clause is not scripted")
                      .AppendLine();
                break;
        }

        return clause.ToString();
    }

    private static string? LockModeWord(string lockMode) => lockMode switch
    {
        "Row" => "row",
        "Page" => "page",
        "Table" => "table",

        // "Unknown ('Z')" and the like. Omitting the clause gives the server default,
        // which is honest; writing a guess would not be.
        _ => null,
    };

    private static void AppendIndexes(StringBuilder sql, TableDetail detail)
    {
        // An index that backs a constraint is created by the constraint. Scripting it
        // again would produce a duplicate on the same key.
        var standalone = detail.Indexes.Where(i => !i.BacksConstraint).ToList();

        if (standalone.Count == 0)
        {
            return;
        }

        sql.AppendLine();

        foreach (IndexDetail index in standalone)
        {
            sql.Append(IndexStatement(detail.Object, index));
        }
    }

    private static string IndexStatement(SchemaObject table, IndexDetail index)
    {
        var sql = new StringBuilder("create ");

        if (index.IsUnique)
        {
            sql.Append("unique ");
        }

        if (index.IsClustered)
        {
            sql.Append("cluster ");
        }

        return sql
            .Append("index ").Append(Qualify(index.Owner, index.Name))
            .Append(" on ").Append(Qualify(table.Owner, table.Name))
            .Append(" (")
            .Append(string.Join(
                ",",
                index.Keys.Select(k => Identifier(k.Name) + (k.Descending ? " desc" : string.Empty))))
            .AppendLine(") using btree;")
            .ToString();
    }

    private static void AppendTriggerNote(StringBuilder sql, TableDetail detail)
    {
        if (detail.Triggers.Count == 0)
        {
            return;
        }

        sql.AppendLine()
           .Append("-- Not scripted: ")
           .Append(detail.Triggers.Count.ToString(CultureInfo.InvariantCulture))
           .Append(detail.Triggers.Count == 1 ? " trigger — " : " triggers — ")
           .Append(string.Join(", ", detail.Triggers.Select(t => $"{t.Name} ({t.Event})")))
           .AppendLine(".")
           .Append("-- Trigger text lives in systrigbody, which IMS does not read.")
           .AppendLine();
    }

    // ---- Shared ------------------------------------------------------------------

    private static void AppendProvenance(StringBuilder sql, SchemaObject subject, string? label = null)
    {
        sql.Append("-- ").Append(label ?? subject.Name)
           .AppendLine(" — scripted by IMS from the system catalogue.")
           .AppendLine("-- Privileges are not included. Check against dbschema before relying on it.")
           .AppendLine();
    }

    /// <summary>
    /// An owner-qualified name in dbschema's form.
    /// </summary>
    /// <remarks>
    /// dbschema always delimits the owner and never the object, which is not
    /// symmetry for its own sake: an owner is a user name and may be mixed case or
    /// contain characters Informix would otherwise fold, whereas an object name that
    /// needs delimiting is the exception. IMS quotes the object too when it has to,
    /// because a script that will not run is worse than one that differs from
    /// dbschema.
    /// </remarks>
    private static string Qualify(string owner, string name) =>
        string.IsNullOrWhiteSpace(owner)
            ? Identifier(name)
            : "\"" + owner.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"." + Identifier(name);

    private static string Identifier(string name) => SchemaObject.Quote(name);
}
