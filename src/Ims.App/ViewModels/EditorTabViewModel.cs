using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ims.Core.Connections;
using Ims.Core.Data;
using Ims.Core.History;
using Ims.Core.Sql;

namespace Ims.App.ViewModels;

/// <summary>
/// One editor tab: its text, the connection it targets, and what came back.
/// </summary>
public sealed partial class EditorTabViewModel : ObservableObject, ITabViewModel
{
    private readonly QueryHistory _history;
    private readonly Func<StatementWarning, string, bool> _confirmDestructive;
    private CancellationTokenSource? _execution;
    private CancelOutcome? _lastCancelOutcome;

    /// <summary>
    /// Identity for the autosave store — stable for the life of the tab.
    /// </summary>
    /// <remarks>
    /// The autosave key used to be derived from <see cref="Title"/>, which changes:
    /// saving to a file renames the tab, and so did the " (recovered)" suffix. Each
    /// rename made the tab look new to <c>EditorAutosave</c>, so it wrote a second
    /// file and orphaned the first. Recovering then re-suffixed the title, which
    /// produced another key on the next run, and the autosave directory filled with
    /// <c>Query_4__recovered___recovered_.json</c> and friends — one generation per
    /// launch, each reappearing as its own tab.
    /// </remarks>
    public string AutosaveId { get; private set; } = Guid.NewGuid().ToString("n");

    /// <summary>
    /// Takes over a previous run's autosave identity, when reopening its tab.
    /// </summary>
    /// <remarks>
    /// Only for <see cref="MainViewModel.RestoreAutosavedTabs"/>. A restored tab has to
    /// continue owning the file it came from; minting a fresh id would leave the old
    /// file behind to be recovered again on the next launch, and again after that.
    /// </remarks>
    public void AdoptAutosaveId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        AutosaveId = id;
    }

    [ObservableProperty]
    private string _title = "Untitled";

    /// <summary>
    /// Explains a cancel that stopped IMS waiting without stopping the statement.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StatusText"/> because the status bar is transient and
    /// this needs to stay put: a user whose query is still consuming server CPU has to
    /// be able to read what to do about it after the moment has passed. Null whenever
    /// there is nothing to say, so the banner can bind to its own presence.
    /// </remarks>
    [ObservableProperty]
    private string? _cancelNotice;

    [ObservableProperty]
    private string _sql = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private IInformixSession? _session;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private ResultSetViewModel? _selectedResult;

    /// <summary>
    /// Whether the last run had a statement fail, so the shell knows which bottom
    /// pane to show.
    /// </summary>
    /// <remarks>
    /// A failure's detail — SQLCODE, ISAM error, which statement — lives only on the
    /// Messages tab (PR-3.4, PR-3.6), and the Results tab beside it stays empty or
    /// shows the previous run's grid; a success is the other way round. This is a
    /// signal the view acts on rather than a counter for display;
    /// <see cref="StatusText"/> already says how many failed.
    /// </remarks>
    public bool LastRunFailed { get; private set; }

    /// <param name="confirmDestructive">
    /// Asks the user to confirm an unqualified UPDATE or DELETE (PR-3.8). Injected
    /// so the view model stays testable and so the prompt is the shell's business.
    /// </param>
    public EditorTabViewModel(
        QueryHistory history,
        Func<StatementWarning, string, bool> confirmDestructive)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _confirmDestructive = confirmDestructive ?? throw new ArgumentNullException(nameof(confirmDestructive));
    }

    /// <summary>Results, newest last. PR-4.7 keeps earlier ones rather than discarding them.</summary>
    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    /// <summary>Errors and row counts from the last run, in statement order (PR-3.4).</summary>
    public ObservableCollection<StatementOutcome> Outcomes { get; } = [];

    /// <summary>Which instance this tab targets, shown unambiguously (PR-1.6).</summary>
    public string TargetLabel => Session?.Descriptor.TargetLabel ?? "Not connected";

    public InformixEnvironment Environment =>
        Session?.Descriptor.Environment ?? InformixEnvironment.Unspecified;

    /// <summary>PR-3.7: visible at all times.</summary>
    public TransactionState TransactionState => Session?.TransactionState ?? TransactionState.NotApplicable;

    public bool CanExecute => Session is { State: SessionState.Open } && !IsExecuting;

    /// <summary>Runs the whole script, or the selection when there is one (PR-3.3).</summary>
    [RelayCommand]
    public async Task ExecuteAsync(string? selectedText)
    {
        string script = string.IsNullOrWhiteSpace(selectedText) ? Sql : selectedText;

        if (Session is not { } session || string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(script);

        if (statements.Count == 0)
        {
            StatusText = "Nothing to run.";
            return;
        }

        // PR-3.8, before anything is sent.
        foreach ((SqlStatement statement, StatementWarning warning) in
                 StatementSafety.CheckScript(statements))
        {
            if (!_confirmDestructive(warning, statement.Text))
            {
                StatusText = "Cancelled before sending anything.";
                return;
            }
        }

        await ClearResultsAsync().ConfigureAwait(true);

        // A new run means the last one's warning is stale, whatever became of it.
        CancelNotice = null;
        LastRunFailed = false;

        _execution = new CancellationTokenSource();
        IsExecuting = true;
        OnPropertyChanged(nameof(CanExecute));

        var stopwatch = Stopwatch.StartNew();
        var failed = 0;

        try
        {
            // Off the dispatcher: NFR-1 and the ServerCallGuard both require it.
            await Task.Run(async () =>
            {
                await foreach (StatementOutcome outcome in session
                    .ExecuteScriptAsync(script, _execution.Token)
                    .ConfigureAwait(false))
                {
                    StatementOutcome captured = outcome;

                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Outcomes.Add(captured);

                        if (captured.Kind == StatementResultKind.RowSet && captured.Result is not null)
                        {
                            var resultViewModel = new ResultSetViewModel(
                                captured.Result,
                                captured.Sql,
                                captured.Elapsed,
                                captured.Index + 1);

                            Results.Add(resultViewModel);
                            SelectedResult ??= resultViewModel;
                        }
                    });

                    if (captured.Kind == StatementResultKind.Failed)
                    {
                        failed++;
                    }

                    RecordHistory(captured, session.Descriptor);
                }
            }, _execution.Token).ConfigureAwait(true);

            // Fetch the first page of each result so the grid is not empty.
            foreach (ResultSetViewModel result in Results)
            {
                await result.FetchMoreAsync(_execution.Token).ConfigureAwait(true);
            }

            stopwatch.Stop();

            LastRunFailed = failed > 0;

            StatusText = failed == 0
                ? $"{statements.Count} statement(s) in {stopwatch.ElapsedMilliseconds:N0} ms"
                : $"{failed} of {statements.Count} statement(s) failed — "
                  + $"{stopwatch.ElapsedMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            // PR-3.5 is not met: measured against 14.10, the driver's cancel does not
            // reach the server. Saying "Cancelled" would be a lie about someone's
            // runaway query, so say what actually happened and name the tool that can
            // finish the job — the same habit PR-8.2 applies to onstat.
            StatusText = _lastCancelOutcome == CancelOutcome.StoppedWaitingOnly
                ? $"Stopped waiting after {stopwatch.ElapsedMilliseconds:N0} ms — "
                  + "the statement is still running on the server"
                : $"Cancelled after {stopwatch.ElapsedMilliseconds:N0} ms";

            if (_lastCancelOutcome == CancelOutcome.StoppedWaitingOnly)
            {
                CancelNotice =
                    "The editor is free and the session is intact, but this Informix ODBC "
                    + "driver does not pass a cancel to the server, so the statement keeps "
                    + "running there until it finishes. To stop it, find the session with "
                    + "'onstat -g ses' and end it with 'onmode -z <sid>'.";
            }

            _lastCancelOutcome = null;
        }
        catch (InformixException ex)
        {
            stopwatch.Stop();
            LastRunFailed = true;
            StatusText = ex.Error.ToString();
        }
        finally
        {
            IsExecuting = false;
            _execution?.Dispose();
            _execution = null;

            OnPropertyChanged(nameof(CanExecute));
            OnPropertyChanged(nameof(TransactionState));
        }
    }

    /// <summary>PR-3.5: stop the statement, keep the session and the application.</summary>
    [RelayCommand]
    public async Task CancelAsync()
    {
        if (_execution is null || Session is null)
        {
            return;
        }

        StatusText = "Asking the server to stop…";

        await _execution.CancelAsync().ConfigureAwait(true);

        // Remembered rather than acted on here: the OperationCanceledException this
        // provokes is caught in ExecuteAsync, and that is where the user is told. Two
        // places writing StatusText would race.
        _lastCancelOutcome = await Session
            .CancelAsync(CancellationToken.None)
            .ConfigureAwait(true);
    }

    public async Task ClearResultsAsync()
    {
        foreach (ResultSetViewModel result in Results.ToList())
        {
            await result.DisposeAsync().ConfigureAwait(true);
        }

        Results.Clear();
        Outcomes.Clear();
        SelectedResult = null;
    }

    public async ValueTask DisposeAsync()
    {
        _execution?.Dispose();
        await ClearResultsAsync().ConfigureAwait(false);
    }

    partial void OnSqlChanged(string value)
    {
        IsDirty = true;
    }

    partial void OnSessionChanged(IInformixSession? value)
    {
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(Environment));
        OnPropertyChanged(nameof(TransactionState));
        OnPropertyChanged(nameof(CanExecute));
    }

    private void RecordHistory(StatementOutcome outcome, ConnectionDescriptor descriptor) =>
        _history.Add(new QueryHistoryEntry
        {
            ExecutedAt = DateTimeOffset.Now,
            Sql = outcome.Sql,
            Target = descriptor.TargetLabel,
            Database = descriptor.Database,
            ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
            RowCount = outcome.RowsAffected,
            Succeeded = outcome.Succeeded,
            Error = outcome.Error?.ToString(),
        });
}
