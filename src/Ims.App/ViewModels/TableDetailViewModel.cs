using CommunityToolkit.Mvvm.ComponentModel;
using Ims.Core.Catalog;

namespace Ims.App.ViewModels;

/// <summary>
/// Everything PR-2.4 asks for about one table, ready for the detail pane.
/// </summary>
/// <remarks>
/// <para>
/// Detail is fetched when the user asks for it, not as they move through the tree.
/// PR-2.2 says load detail strictly on demand, and PR-6.4 says metadata queries
/// must stay negligible on a production instance — arrowing down a list of 500
/// tables should not issue 500 rounds of six catalogue queries each.
/// </para>
/// <para>
/// So the pane loads when it is visible and the selection changes, and does nothing
/// at all while it is hidden.
/// </para>
/// </remarks>
public sealed partial class TableDetailViewModel : ObservableObject
{
    private readonly ICatalogReader _catalog;

    [ObservableProperty]
    private TableDetail? _detail;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _title = "No object selected";

    public TableDetailViewModel(ICatalogReader catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Which object is shown. Null clears the pane.</summary>
    public SchemaObject? Subject { get; private set; }

    /// <summary>True when there is something to show.</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>
    /// A one-line summary of storage and locking, in the terms PR-2.4 names.
    /// </summary>
    public string StorageSummary
    {
        get
        {
            if (Detail is not { } detail)
            {
                return string.Empty;
            }

            var parts = new List<string> { $"Lock mode: {detail.LockMode}" };

            if (detail.IsFragmented)
            {
                parts.Add($"Fragmented {detail.Fragments.Count} ways "
                          + $"({detail.Fragments[0].Strategy.ToLowerInvariant()})");
            }
            else if (detail.DbSpace is { Length: > 0 } dbspace)
            {
                parts.Add($"Dbspace: {dbspace}");
            }

            if (detail.FirstExtentKb is { } first)
            {
                parts.Add($"Extents: {first} KB first"
                          + (detail.NextExtentKb is { } next ? $", {next} KB next" : string.Empty));
            }

            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>
    /// The statistics line (PR-2.5), which says how much to trust the row count.
    /// </summary>
    public string StatisticsSummary => Detail switch
    {
        null => string.Empty,

        { Statistics: StatisticsCurrency.Never } =>
            "Statistics: never gathered — the row count is a guess. Run UPDATE STATISTICS.",

        { Statistics: StatisticsCurrency.Stale, StatisticsUpdatedAt: { } when } =>
            $"Statistics: last gathered {when:yyyy-MM-dd} — over 30 days old, so the row count "
            + "and any plan based on it may be wrong.",

        { Statistics: StatisticsCurrency.Current, StatisticsUpdatedAt: { } when } =>
            $"Statistics: last gathered {when:yyyy-MM-dd}.",

        _ => "Statistics: this server does not report when they were last gathered.",
    };

    /// <summary>Every catalogue query behind this pane, for PR-8.2.</summary>
    public string QueriesUsed =>
        Detail is { } detail
            ? string.Join(
                Environment.NewLine + Environment.NewLine + "-- ────────" + Environment.NewLine,
                detail.QueriesUsed.Distinct())
            : string.Empty;

    /// <summary>Loads detail for an object, or clears the pane when given null.</summary>
    public async Task ShowAsync(SchemaObject? schemaObject, CancellationToken cancellationToken)
    {
        Subject = schemaObject;

        if (schemaObject is null)
        {
            Detail = null;
            Title = "No object selected";
            RaiseDerived();
            return;
        }

        // Only table-shaped objects have the detail PR-2.4 describes.
        if (schemaObject.Kind is not (SchemaObjectKind.Table or SchemaObjectKind.View))
        {
            Detail = null;
            Title = $"{schemaObject.QualifiedName} — {schemaObject.Kind}";
            Error = "Detail is available for tables and views.";
            RaiseDerived();
            return;
        }

        IsLoading = true;
        Error = null;
        Title = schemaObject.QualifiedName;

        try
        {
            // Off the dispatcher: six catalogue round trips (NFR-1).
            Detail = await Task.Run(
                () => _catalog.GetTableDetailAsync(schemaObject.TabId, cancellationToken),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Detail = null;
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            RaiseDerived();
        }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(StorageSummary));
        OnPropertyChanged(nameof(StatisticsSummary));
        OnPropertyChanged(nameof(QueriesUsed));
    }
}

/// <summary>
/// Plain-language notes on the Informix concepts the detail pane shows (NFR-11).
/// </summary>
/// <remarks>
/// NFR-11 asks for "in-context explanation of the Informix concepts surfaced in
/// PR-2.4 — U2 will not know what a dbspace is". U2 is the generalist DBA who has
/// inherited some Informix; these are written for someone fluent in SQL Server or
/// PostgreSQL meeting Informix's vocabulary for the first time.
/// </remarks>
public static class InformixConcepts
{
    public const string DbSpace =
        "A dbspace is a named logical storage area made of one or more chunks (files or raw "
        + "devices). It is the closest thing Informix has to a SQL Server filegroup or a "
        + "PostgreSQL tablespace, and every table lives in one unless it is fragmented across "
        + "several.";

    public const string Extent =
        "An extent is a contiguous run of disk allocated to a table in one go. Informix "
        + "allocates the first extent when the table is created and another of the 'next' size "
        + "each time it fills. Sizing them too small makes a large table sprawl across many "
        + "extents, which costs performance.";

    public const string Fragmentation =
        "Fragmentation is Informix's partitioning: a table's rows are spread across several "
        + "dbspaces by round robin, by an expression, by hash, by interval or by list. The "
        + "strategy decides which fragment a row lands in, and an expression or interval "
        + "strategy lets the optimiser skip fragments that cannot match a query.";

    public const string LockMode =
        "Lock mode is the granularity Informix locks at for this table: page (the default, and "
        + "cheaper) or row (more concurrent, more locks). It is set per table and changes how "
        + "much two sessions writing to the same table get in each other's way.";

    public const string Statistics =
        "UPDATE STATISTICS gathers the distribution data the optimiser uses to choose a query "
        + "plan. The row count shown here comes from that snapshot, not from counting rows now, "
        + "so on a table that has grown since the last run it can be badly out of date.";

    public const string Serial =
        "SERIAL is Informix's auto-incrementing integer, equivalent to SQL Server's IDENTITY or "
        + "PostgreSQL's serial. SERIAL8 and BIGSERIAL are the 64-bit forms.";

    // ---- The session monitor's vocabulary (Slice 3, NFR-11) ---------------------

    public const string Session =
        "A session is one client's connection to the instance, identified by a session id — the "
        + "'sid' that onstat -g ses lists. One application can hold several, and a session "
        + "outlives any single statement it runs.";

    public const string SessionVersusProcess =
        "The session id is Informix's own number for a connection; the process id belongs to the "
        + "client program on its own machine. They are unrelated, and onmode -z takes the session "
        + "id, not the process id — confusing the two is how the wrong thing gets stopped.";

    public const string Lock =
        "A lock reserves a row, page or table so two sessions cannot change it at once. A session "
        + "that wants a lock somebody else holds in an incompatible mode waits for it — which is "
        + "what 'blocked' means here. Two shared read locks are compatible and do not block.";

    public const string Latch =
        "A latch protects a structure inside the server's shared memory for the moment it takes to "
        + "read or change it, so it is held for microseconds rather than for a transaction. A "
        + "session waiting on one is not blocked by another user's work in the way a lock wait is.";

    public const string Checkpoint =
        "At a checkpoint Informix flushes modified pages from shared memory to disk, giving "
        + "recovery a known-good starting point. A long gap since the last one means more work to "
        + "redo after a failure; checkpoints that are very frequent usually mean the logical log "
        + "is too small.";

    public const string BufferEfficiency =
        "The read cache percentage is how often a page was already in shared memory rather than "
        + "having to be fetched from disk. High is good, and on a busy OLTP instance anything "
        + "below about 95% is usually worth investigating. On a freshly started server there has "
        + "been too little work to compute it, which is why it can read Unknown.";

    public const string Uptime =
        "How long the instance has been running since it was last brought online. Computed by IMS "
        + "from the server's recorded boot time.";

    public const string TempSpace =
        "Temporary dbspaces hold sort work, temporary tables and index builds. IMS reports them "
        + "for the instance rather than per session: attributing temporary space to one session "
        + "needs partition detail IMS does not read, and a number it cannot justify is worse than "
        + "an admitted gap.";
}
