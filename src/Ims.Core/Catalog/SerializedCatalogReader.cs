using Ims.Core.Monitoring;

namespace Ims.Core.Catalog;

/// <summary>
/// Lets several callers share one catalogue reader safely.
/// </summary>
/// <remarks>
/// <para>
/// An Informix connection has one cursor. Two overlapping catalogue queries on the
/// same connection do not queue — the second closes the first's result out from
/// under it, which is the bug that took a day to find in Slice 1 and is worth not
/// finding twice.
/// </para>
/// <para>
/// The alternative is a connection each, and that is what this exists to avoid.
/// Slice 2 already spends one session per instance on the object tree; PR-3.2's
/// completion cache would make it two, and PR-6.4 asks IMS to stay negligible on a
/// production server. A semaphore is cheaper than a session, and metadata queries
/// are short enough that waiting behind one is not felt.
/// </para>
/// <para>
/// Session monitoring (<see cref="ISessionMonitor"/>) goes through the same gate, because
/// it goes down the same connection and therefore the same one cursor. Giving the monitor a
/// gate of its own would be two gates over one cursor, which is precisely the failure this
/// class exists to prevent — so the monitor waits behind a tree expansion instead. That is
/// the cost of not spending a third session per instance (PR-6.4).
/// </para>
/// </remarks>
public sealed class SerializedCatalogReader(ICatalogReader inner)
    : ICatalogReader, ISessionMonitor, IAsyncDisposable
{
    private readonly ICatalogReader _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<CatalogResult<DatabaseInfo>> GetDatabasesAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetDatabasesAsync(cancellationToken), cancellationToken);

    public Task<CatalogResult<SchemaObject>> GetObjectsAsync(
        SchemaObjectKind kind,
        string? nameFilter,
        string? owner,
        bool includeSystem,
        CancellationToken cancellationToken) =>
        RunAsync(
            () => _inner.GetObjectsAsync(kind, nameFilter, owner, includeSystem, cancellationToken),
            cancellationToken);

    public Task<TableDetail> GetTableDetailAsync(int tabId, CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetTableDetailAsync(tabId, cancellationToken), cancellationToken);

    public Task<CatalogResult<string>> GetRoutineSourceAsync(
        int procId,
        CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetRoutineSourceAsync(procId, cancellationToken), cancellationToken);

    public Task<CatalogResult<string>> GetViewSourceAsync(
        int tabId,
        CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetViewSourceAsync(tabId, cancellationToken), cancellationToken);

    public Task<CatalogResult<string>> GetOwnersAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetOwnersAsync(cancellationToken), cancellationToken);

    // ---- Session monitoring, through the same gate (PR-5.1 to PR-5.6) -----------

    /// <summary>
    /// The reader underneath, when it monitors sessions as well as reading schema.
    /// </summary>
    /// <remarks>
    /// The constructor still takes a plain <see cref="ICatalogReader"/> rather than
    /// requiring both. Tightening it would break every caller that has only a catalogue
    /// reader — the completion cache and every test double — for the sake of a capability
    /// they never ask about.
    /// </remarks>
    private ISessionMonitor? Monitor => _inner as ISessionMonitor;

    /// <inheritdoc />
    public bool? SysMasterReadable => Monitor?.SysMasterReadable;

    /// <inheritdoc />
    public Task<SessionSnapshot> GetSessionsAsync(CancellationToken cancellationToken) =>
        Monitor is { } monitor
            ? RunAsync(() => monitor.GetSessionsAsync(cancellationToken), cancellationToken)
            : Task.FromResult(SessionSnapshot.Unavailable(NotAMonitor, DateTimeOffset.Now));

    /// <inheritdoc />
    public Task<SessionDetail> GetSessionDetailAsync(
        int sid,
        IReadOnlyList<LockWaitEdge> knownWaits,
        CancellationToken cancellationToken) =>
        Monitor is { } monitor
            ? RunAsync(
                () => monitor.GetSessionDetailAsync(sid, knownWaits, cancellationToken),
                cancellationToken)
            : Task.FromResult(new SessionDetail
            {
                Sid = sid,
                LocksHeld = [],
                Waits = [],
                Queries = [],
            });

    /// <inheritdoc />
    public Task<InstanceIndicators> GetInstanceIndicatorsAsync(CancellationToken cancellationToken) =>
        Monitor is { } monitor
            ? RunAsync(() => monitor.GetInstanceIndicatorsAsync(cancellationToken), cancellationToken)
            : Task.FromResult(InstanceIndicators.None);

    private const string NotAMonitor =
        "This connection's reader does not monitor sessions.";

    private async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
