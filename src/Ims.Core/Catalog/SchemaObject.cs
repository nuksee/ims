namespace Ims.Core.Catalog;

/// <summary>
/// The kinds of object the browser shows (PR-2.1).
/// </summary>
public enum SchemaObjectKind
{
    Table,
    View,

    /// <summary>A public synonym.</summary>
    Synonym,

    /// <summary>A synonym visible only to its owner. Informix distinguishes the two.</summary>
    PrivateSynonym,

    Sequence,
    Procedure,
    Function,
    Index,
    Constraint,
    Trigger,
    UserDefinedType,
}

/// <summary>
/// One object in a database, as the tree lists it.
/// </summary>
/// <remarks>
/// Deliberately lightweight. PR-2.2 requires children and detail to load strictly on
/// demand so expanding a large database never stalls the UI, and NFR-2 puts that at
/// 20,000+ objects — so the listing carries only what a tree row shows, and anything
/// more comes from <see cref="ICatalogReader.GetTableDetailAsync"/> when the user
/// selects something.
/// </remarks>
public sealed record SchemaObject
{
    /// <summary>The catalogue's own identifier, used to fetch detail without a name lookup.</summary>
    public required int TabId { get; init; }

    public required string Name { get; init; }

    public required string Owner { get; init; }

    public required SchemaObjectKind Kind { get; init; }

    /// <summary>Estimated rows, from the catalogue. Only as fresh as the statistics (PR-2.5).</summary>
    public long? EstimatedRows { get; init; }

    public DateTime? Created { get; init; }

    /// <summary>
    /// Owner-qualified, and quoted only where it has to be.
    /// </summary>
    /// <remarks>
    /// Informix folds unquoted identifiers to lower case, so a mixed-case or
    /// otherwise irregular name must be delimited or the generated SQL will not find
    /// the object it names.
    /// </remarks>
    public string QualifiedName => $"{Quote(Owner)}.{Quote(Name)}";

    /// <summary>Quotes an identifier when Informix would not read it back unchanged.</summary>
    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return "\"\"";
        }

        bool safe = char.IsLetter(identifier[0]) || identifier[0] == '_';

        foreach (char c in identifier)
        {
            safe &= char.IsLower(c) || char.IsAsciiDigit(c) || c == '_';
        }

        return safe
            ? identifier
            : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>
/// A result from the catalogue, together with the query that produced it.
/// </summary>
/// <remarks>
/// PR-8.2 — "any structured view must offer the underlying raw output ... the
/// catalogue query ... on demand. This is what earns U3's trust." Carrying the SQL
/// alongside the data makes that structural rather than something the UI has to
/// reconstruct, and it means the query shown is always the query that ran.
/// </remarks>
public sealed record CatalogResult<T>(IReadOnlyList<T> Items, string Sql);

/// <summary>Creation helpers for <see cref="CatalogResult{T}"/>.</summary>
public static class CatalogResult
{
    public static CatalogResult<T> Empty<T>(string sql) => new([], sql);
}
