using Ims.Core.Catalog;

namespace Ims.Core.Completion;

/// <summary>
/// Turns a caret position into a list of suggestions (PR-3.2).
/// </summary>
/// <remarks>
/// Pure and synchronous: context and cached metadata in, ordered list out. Everything
/// that could touch a server has already happened by the time this runs, which is
/// what lets it be called on every keystroke without a thought (NFR-1).
/// </remarks>
public static class CompletionEngine
{
    /// <summary>How many items are worth showing. Beyond this, filtering is the answer.</summary>
    public const int MaximumItems = 300;

    public static IReadOnlyList<CompletionItem> Suggest(
        CompletionContext context,
        ICatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);

        IEnumerable<CompletionItem> candidates = context.Target switch
        {
            CompletionTarget.Member => Members(context, catalog),
            CompletionTarget.ObjectName => ObjectNames(catalog).Concat(Owners(catalog)),
            CompletionTarget.ColumnOrExpression => ColumnsInScope(context, catalog)
                .Concat(Aliases(context))
                .Concat(InformixVocabulary.Functions)
                .Concat(ObjectNames(catalog))
                .Concat(InformixVocabulary.Keywords),
            _ => InformixVocabulary.All.Concat(ObjectNames(catalog)),
        };

        return Rank(candidates, context.Prefix);
    }

    /// <summary>
    /// What can follow a dot.
    /// </summary>
    /// <remarks>
    /// The qualifier is resolved against three things in order: an alias in the
    /// statement, a table by name, then an owner. That order matters — an alias
    /// shadows a table of the same name for the length of the statement, which is
    /// exactly what the person who wrote the alias intended.
    /// </remarks>
    private static IEnumerable<CompletionItem> Members(
        CompletionContext context,
        ICatalogSnapshot catalog)
    {
        string qualifier = (context.Qualifier ?? string.Empty).Trim('"');

        TableReference? alias = context.Tables.FirstOrDefault(t =>
            string.Equals(t.Alias, qualifier, StringComparison.OrdinalIgnoreCase));

        if (alias is not null)
        {
            return ColumnsOf(alias, catalog);
        }

        // The owner check comes before the table check, and has to. Typing
        // "informix." leaves the statement reading FROM informix, so the qualifier
        // also looks like a table named informix — and answering with that table's
        // columns instead of the owner's objects would be wrong every time.
        var owned = catalog.Objects
            .Where(o => string.Equals(o.Owner, qualifier, StringComparison.OrdinalIgnoreCase))
            .Select(ToItem)
            .ToList();

        if (owned.Count > 0)
        {
            return owned;
        }

        TableReference? named = context.Tables.FirstOrDefault(t =>
            string.Equals(t.Name, qualifier, StringComparison.OrdinalIgnoreCase));

        if (named is not null)
        {
            return ColumnsOf(named, catalog);
        }

        // A table the statement does not name — someone writing a qualified column for
        // a table they are about to add to the FROM clause. Worth answering.
        SchemaObject? known = catalog.Objects.FirstOrDefault(o =>
            string.Equals(o.Name, qualifier, StringComparison.OrdinalIgnoreCase));

        return known is null
            ? []
            : ColumnsOf(new TableReference(known.Name, known.Owner, null), catalog);
    }

    private static IEnumerable<CompletionItem> ColumnsInScope(
        CompletionContext context,
        ICatalogSnapshot catalog)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TableReference table in context.Tables)
        {
            foreach (CompletionItem item in ColumnsOf(table, catalog))
            {
                // The same column name on two joined tables is one suggestion, not two.
                // Which one is meant is a question the qualifier answers, and offering
                // the choice twice unqualified answers nothing.
                if (seen.Add(item.Text))
                {
                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<CompletionItem> ColumnsOf(TableReference table, ICatalogSnapshot catalog)
    {
        IReadOnlyList<string> columns = catalog.ColumnsOf(table.Name, table.Owner);

        if (columns.Count == 0)
        {
            // Not cached. Ask for it, return nothing now: the next keystroke gets them.
            catalog.RequestColumns(table.Name, table.Owner);
            return [];
        }

        return columns.Select(c => new CompletionItem(c, CompletionKind.Column, table.Qualifier));
    }

    private static IEnumerable<CompletionItem> Aliases(CompletionContext context) =>
        context.Tables
            .Where(t => t.Alias is { Length: > 0 })
            .Select(t => new CompletionItem(t.Alias!, CompletionKind.Alias, t.Name));

    private static IEnumerable<CompletionItem> ObjectNames(ICatalogSnapshot catalog) =>
        catalog.Objects.Select(ToItem);

    private static IEnumerable<CompletionItem> Owners(ICatalogSnapshot catalog) =>
        catalog.Owners.Select(o => new CompletionItem(o, CompletionKind.Owner, "owner"));

    private static CompletionItem ToItem(SchemaObject o) => new(
        o.Name,
        o.Kind switch
        {
            SchemaObjectKind.View => CompletionKind.View,
            SchemaObjectKind.Synonym or SchemaObjectKind.PrivateSynonym => CompletionKind.Synonym,
            SchemaObjectKind.Sequence => CompletionKind.Sequence,
            SchemaObjectKind.Procedure or SchemaObjectKind.Function => CompletionKind.Routine,
            _ => CompletionKind.Table,
        },
        o.Owner);

    /// <summary>
    /// Filters by the typed prefix and orders what is left.
    /// </summary>
    /// <remarks>
    /// A prefix match beats a contains-match, and both beat nothing — someone who has
    /// typed "cust" wants <c>customer</c> before <c>order_customer</c>, but wants
    /// <c>order_customer</c> more than they want to be told there is no match.
    /// </remarks>
    private static List<CompletionItem> Rank(
        IEnumerable<CompletionItem> candidates,
        string prefix)
    {
        var seen = new HashSet<(string, CompletionKind)>();
        var matches = new List<(CompletionItem Item, int Quality)>();

        foreach (CompletionItem item in candidates)
        {
            int quality = Match(item.Text, prefix);

            if (quality < 0 || !seen.Add((item.Text, item.Kind)))
            {
                continue;
            }

            matches.Add((item, quality));
        }

        return matches
            .OrderBy(m => m.Quality)
            .ThenBy(m => m.Item.Rank)
            .ThenBy(m => m.Item.Text, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumItems)
            .Select(m => m.Item)
            .ToList();
    }

    private static int Match(string text, string prefix)
    {
        if (prefix.Length == 0)
        {
            return 0;
        }

        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return text.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }
}
