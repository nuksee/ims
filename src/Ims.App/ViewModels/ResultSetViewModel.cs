using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ims.Core.Data;

namespace Ims.App.ViewModels;

/// <summary>One row, exposed to the grid by column index.</summary>
/// <remarks>
/// The indexer is what lets the grid bind to a result whose shape is not known
/// until the statement runs.
/// </remarks>
public sealed class ResultRowViewModel(InformixValue[] values, long rowNumber)
{
    public long RowNumber { get; } = rowNumber;

    public InformixValue this[int index] =>
        index >= 0 && index < values.Length ? values[index] : InformixValue.Null(InformixDbType.Unknown);

    public InformixValue[] Values => values;
}

/// <summary>
/// A result set as the grid sees it: a page at a time, never all at once.
/// </summary>
/// <remarks>
/// <para>
/// PR-4.2 requires results to stream and page "rather than materialising them, so
/// an unbounded <c>SELECT</c> degrades gracefully instead of exhausting memory",
/// and RSK-6 is the hung client. So the view model pulls a page, hands it to the
/// grid, and stops until asked for more.
/// </para>
/// <para>
/// The row count shown is always the number actually fetched, and the UI says
/// whether more remain. Showing a total would mean counting the whole set first,
/// which is precisely what PR-4.2 forbids.
/// </para>
/// </remarks>
public sealed partial class ResultSetViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Rows per fetch. Large enough to fill a screen, small enough to feel instant.</summary>
    public const int PageSize = 500;

    private readonly IStatementResult _result;
    private IAsyncEnumerator<InformixValue[]>? _enumerator;
    private bool _disposed;

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private bool _hasMoreRows = true;

    [ObservableProperty]
    private string _status = string.Empty;

    public ResultSetViewModel(IStatementResult result, string sql, TimeSpan elapsed)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        Sql = sql;
        Elapsed = elapsed;
        Columns = result.Columns;
        UpdateStatus();
    }

    /// <summary>The statement that produced this, shown verbatim (PR-8.2).</summary>
    public string Sql { get; }

    /// <summary>Server time for the statement (PR-4.3).</summary>
    public TimeSpan Elapsed { get; }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public ObservableCollection<ResultRowViewModel> Rows { get; } = [];

    /// <summary>Fetches the next page. Safe to call when there is nothing left.</summary>
    [RelayCommand]
    public async Task FetchMoreAsync(CancellationToken cancellationToken)
    {
        if (_disposed || IsFetching || !HasMoreRows)
        {
            return;
        }

        IsFetching = true;

        try
        {
            long firstRowNumber = Rows.Count;

            // The whole page is read off the dispatcher. System.Data.Odbc has no
            // true async — ReadAsync is synchronous underneath — so resuming on the
            // UI thread between rows would block it for the length of the fetch.
            // NFR-1 makes that a defect, not a rough edge, and ServerCallGuard now
            // throws if anyone tries it.
            (List<ResultRowViewModel> page, bool exhausted) = await Task.Run(
                async () =>
                {
                    _enumerator ??= _result
                        .ReadRowsAsync(cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);

                    var rows = new List<ResultRowViewModel>(PageSize);
                    long rowNumber = firstRowNumber;
                    var done = false;

                    while (rows.Count < PageSize)
                    {
                        if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            done = true;
                            break;
                        }

                        rows.Add(new ResultRowViewModel(_enumerator.Current, ++rowNumber));
                    }

                    return (rows, done);
                },
                cancellationToken).ConfigureAwait(true);

            if (exhausted)
            {
                HasMoreRows = false;
            }

            // Back on the UI thread, which is where an ObservableCollection must be
            // mutated.
            foreach (ResultRowViewModel row in page)
            {
                Rows.Add(row);
            }
        }
        catch (OperationCanceledException)
        {
            HasMoreRows = false;
            Status = "Cancelled.";
            return;
        }
        finally
        {
            IsFetching = false;
        }

        UpdateStatus();
    }

    /// <summary>
    /// Streams every remaining row, for export. Does not add them to the grid.
    /// </summary>
    /// <remarks>
    /// Export and display share the source, so exporting after scrolling continues
    /// from where the grid stopped. The rows already on screen are yielded first so
    /// the file is complete rather than missing the visible page.
    /// </remarks>
    public async IAsyncEnumerable<InformixValue[]> EnumerateForExportAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (ResultRowViewModel row in Rows.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row.Values;
        }

        if (!HasMoreRows)
        {
            yield break;
        }

        _enumerator ??= _result.ReadRowsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return _enumerator.Current;
        }

        HasMoreRows = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_enumerator is not null)
        {
            await _enumerator.DisposeAsync().ConfigureAwait(false);
        }

        await _result.DisposeAsync().ConfigureAwait(false);
    }

    private void UpdateStatus() =>
        Status = HasMoreRows
            ? $"{Rows.Count:N0} rows fetched, more available — {Elapsed.TotalMilliseconds:N0} ms"
            : $"{Rows.Count:N0} rows — {Elapsed.TotalMilliseconds:N0} ms";
}
