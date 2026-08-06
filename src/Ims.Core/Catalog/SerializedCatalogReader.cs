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
/// </remarks>
public sealed class SerializedCatalogReader(ICatalogReader inner) : ICatalogReader, IAsyncDisposable
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
