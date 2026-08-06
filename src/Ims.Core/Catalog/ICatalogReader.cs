namespace Ims.Core.Catalog;

/// <summary>
/// Reads schema metadata from an Informix database.
/// </summary>
/// <remarks>
/// <para>
/// Every method is asynchronous and cancellable for the same reason the session's
/// are: NFR-1 forbids blocking the UI on server work, and expanding a node in a
/// tree is exactly the kind of interaction where a stall is most obvious (PR-8.5).
/// </para>
/// <para>
/// Every method returns the SQL it ran alongside its results, because PR-8.2
/// requires the underlying catalogue query to be available on demand — and PR-6.4
/// requires these queries to stay light enough to be negligible on a production
/// instance, which is easier to hold yourself to when the query is on show.
/// </para>
/// </remarks>
public interface ICatalogReader
{
    /// <summary>Databases on the instance.</summary>
    Task<CatalogResult<DatabaseInfo>> GetDatabasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Objects of one kind, optionally filtered (PR-2.1, PR-2.3).
    /// </summary>
    /// <param name="kind">Which kind to list. One kind per call keeps each query small.</param>
    /// <param name="nameFilter">Case-insensitive partial name match, or null for all.</param>
    /// <param name="owner">Restrict to one owner, or null for all.</param>
    /// <param name="includeSystem">
    /// System catalogue tables are excluded by default. They are not what a developer
    /// means by "the tables in this database", and they would bury a small schema.
    /// </param>
    Task<CatalogResult<SchemaObject>> GetObjectsAsync(
        SchemaObjectKind kind,
        string? nameFilter,
        string? owner,
        bool includeSystem,
        CancellationToken cancellationToken);

    /// <summary>Everything PR-2.4 asks for about one table or view.</summary>
    Task<TableDetail> GetTableDetailAsync(int tabId, CancellationToken cancellationToken);

    /// <summary>
    /// The source text of a procedure, function or trigger, as the server stores it.
    /// </summary>
    /// <remarks>
    /// Returned verbatim rather than reformatted. PR-8.2 is about showing what the
    /// server actually holds, and for a routine that is the text itself.
    /// </remarks>
    Task<CatalogResult<string>> GetRoutineSourceAsync(int procId, CancellationToken cancellationToken);

    /// <summary>
    /// A view's defining text, as <c>sysviews</c> stores it.
    /// </summary>
    /// <remarks>
    /// Informix keeps the whole <c>CREATE VIEW</c> statement here, split across
    /// numbered rows, so scripting a view (PR-2.6) is a matter of reassembling what
    /// the server already has rather than rebuilding the statement from a column list.
    /// </remarks>
    Task<CatalogResult<string>> GetViewSourceAsync(int tabId, CancellationToken cancellationToken);

    /// <summary>
    /// Owners that actually own something, for the filter in PR-2.3.
    /// </summary>
    Task<CatalogResult<string>> GetOwnersAsync(CancellationToken cancellationToken);
}

/// <summary>One database on the instance.</summary>
public sealed record DatabaseInfo
{
    public required string Name { get; init; }

    public required string Owner { get; init; }

    /// <summary>
    /// The logging mode, which decides whether transactions apply at all (PR-3.7).
    /// </summary>
    public required DatabaseLogging Logging { get; init; }

    /// <summary>True when identifiers and comparisons follow ANSI rules.</summary>
    public bool IsAnsi { get; init; }
}

/// <summary>How a database logs, which governs transaction behaviour.</summary>
public enum DatabaseLogging
{
    /// <summary>Unlogged. Transactions do not apply — <see cref="Data.TransactionState.NotApplicable"/>.</summary>
    None,

    /// <summary>Buffered logging.</summary>
    Buffered,

    /// <summary>Unbuffered logging.</summary>
    Unbuffered,

    /// <summary>ANSI-mode logging, which commits implicitly rather than autocommitting.</summary>
    Ansi,

    Unknown,
}
