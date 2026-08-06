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
public sealed partial class EditorTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly QueryHistory _history;
    private readonly Func<StatementWarning, string, bool> _confirmDestructive;
    private CancellationTokenSource? _execution;

    [ObservableProperty]
    private string _title = "Untitled";

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
                                captured.Result, captured.Sql, captured.Elapsed);

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

            StatusText = failed == 0
                ? $"{statements.Count} statement(s) in {stopwatch.ElapsedMilliseconds:N0} ms"
                : $"{failed} of {statements.Count} statement(s) failed — "
                  + $"{stopwatch.ElapsedMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            StatusText = $"Cancelled after {stopwatch.ElapsedMilliseconds:N0} ms";
        }
        catch (InformixException ex)
        {
            stopwatch.Stop();
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

        StatusText = "Cancelling…";

        await _execution.CancelAsync().ConfigureAwait(true);
        await Session.CancelAsync(CancellationToken.None).ConfigureAwait(true);
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
