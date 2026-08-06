using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ims.Core.Catalog;
using Ims.Core.Connections;
using Ims.Core.Editing;
using Ims.Core.Data;
using Ims.Core.History;
using Ims.Core.Sql;
using Microsoft.Extensions.Logging;

namespace Ims.App.ViewModels;

/// <summary>A saved connection as the instance list shows it.</summary>
public sealed partial class ConnectionItemViewModel(ConnectionDescriptor descriptor) : ObservableObject
{
    [ObservableProperty]
    private bool _isConnected;

    public ConnectionDescriptor Descriptor { get; set; } = descriptor;

    public string DisplayName => Descriptor.DisplayName;

    public string Detail => $"{Descriptor.ServerName} — {Descriptor.Host}:{Descriptor.Service}";

    public InformixEnvironment Environment => Descriptor.Environment;

    /// <summary>
    /// The environment as words (PR-1.5, NFR-8).
    /// </summary>
    /// <remarks>
    /// NFR-8 forbids relying on colour alone, so the label is the primary signal and
    /// any colour in the view is a secondary one. A production connection has to be
    /// unmistakable to someone who cannot distinguish red from green.
    /// </remarks>
    public string EnvironmentLabel => Descriptor.Environment switch
    {
        InformixEnvironment.Production => "PRODUCTION",
        InformixEnvironment.Uat => "UAT",
        InformixEnvironment.Development => "DEV",
        _ => "UNSPECIFIED",
    };

    public bool IsProduction => Descriptor.IsProduction;
}

/// <summary>
/// The shell's view model: the instance list, the open editors, and the sessions
/// tying them together.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConnectionStore _connections;
    private readonly IInformixSessionFactory _sessionFactory;
    private readonly ICredentialResolver _credentials;
    private readonly QueryHistory _history;
    private readonly EditorAutosave _autosave;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dictionary<Guid, IInformixSession> _sessions = [];
    private readonly DispatcherTimer _autosaveTimer;

    // Monotonic. Tabs.Count + 1 repeats a name as soon as a tab is closed, and two
    // tabs called "Query 2" also collide in the autosave store, which is keyed by title.
    private int _nextTabNumber = 1;

    [ObservableProperty]
    private string _searchTerm = string.Empty;

    [ObservableProperty]
    private EditorTabViewModel? _selectedTab;

    [ObservableProperty]
    private ConnectionItemViewModel? _selectedConnection;

    [ObservableProperty]
    private string _statusText = "Ready";

    /// <summary>The object browser for the current connection (Slice 2).</summary>
    [ObservableProperty]
    private ObjectTreeViewModel? _objectTree;

    public MainViewModel(
        ConnectionStore connections,
        IInformixSessionFactory sessionFactory,
        ICredentialResolver credentials,
        QueryHistory history,
        EditorAutosave autosave,
        ILogger<MainViewModel> logger)
    {
        _connections = connections;
        _sessionFactory = sessionFactory;
        _credentials = credentials;
        _history = history;
        _autosave = autosave;
        _logger = logger;

        RefreshConnectionList();

        // PR-3.9. Frequent enough that a crash costs seconds, infrequent enough that
        // it never competes with typing (PR-8.5).
        _autosaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };

        _autosaveTimer.Tick += (_, _) => AutosaveDirtyTabs();
        _autosaveTimer.Start();
    }

    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = [];

    /// <summary>Asks the shell to confirm a destructive statement (PR-3.8).</summary>
    public Func<StatementWarning, string, bool> ConfirmDestructive { get; set; } =
        static (_, _) => true;

    /// <summary>Asks the shell for a password when the vault has none.</summary>
    public Func<ConnectionDescriptor, string?> PromptForPassword { get; set; } =
        static _ => null;

    public void RefreshConnectionList()
    {
        Connections.Clear();

        foreach (ConnectionDescriptor descriptor in _connections.Search(SearchTerm))
        {
            Connections.Add(new ConnectionItemViewModel(descriptor)
            {
                IsConnected = _sessions.ContainsKey(descriptor.Id),
            });
        }
    }

    partial void OnSearchTermChanged(string value) => RefreshConnectionList();

    /// <summary>Connects, then opens an editor pointed at the new session.</summary>
    [RelayCommand]
    public async Task ConnectAsync(ConnectionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        ConnectionDescriptor descriptor = item.Descriptor;

        if (_sessions.TryGetValue(descriptor.Id, out IInformixSession? existing)
            && existing.State == SessionState.Open)
        {
            NewTab(existing);
            return;
        }

        StatusText = $"Connecting to {descriptor.TargetLabel}…";

        ICredentialResolver resolver = _credentials;

        // If nothing is stored, ask once and use it for this connection only. The
        // password is never written to the descriptor or to disk by IMS (DEC-9).
        string? typed = null;

        if (await _credentials.GetPasswordAsync(descriptor, CancellationToken.None)
                .ConfigureAwait(true) is null)
        {
            typed = PromptForPassword(descriptor);

            if (typed is null)
            {
                StatusText = "Cancelled.";
                return;
            }

            resolver = new TransientCredentialResolver(typed);
        }

        IInformixSession session = _sessionFactory.Create(descriptor, resolver);

        try
        {
            await Task.Run(() => session.OpenAsync(CancellationToken.None)).ConfigureAwait(true);
        }
        catch (InformixException ex)
        {
            await session.DisposeAsync().ConfigureAwait(true);

            StatusText = ex.Error.ToString();

            MessageBox.Show(
                BuildErrorText(ex.Error, descriptor),
                "Could not connect",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        _sessions[descriptor.Id] = session;
        item.IsConnected = true;

        // PR-1.7: notice a drop, say so clearly, keep the editor contents.
        session.StateChanged += OnSessionStateChanged;

        StatusText = $"Connected to {descriptor.TargetLabel}"
                     + (session.ServerInfo is { } info ? $" — {info.VersionBanner}" : string.Empty);

        NewTab(session);

        await OpenObjectTreeAsync(descriptor, resolver).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the object browser on its own connection (Slice 2).
    /// </summary>
    /// <remarks>
    /// Failure here is not fatal. The editor works without a tree, and a user whose
    /// account cannot read the catalogue should still get everything Slice 1 gives
    /// them — PR-8.4 in the other direction: an absent capability is fine, a broken
    /// application is not.
    /// </remarks>
    private async Task OpenObjectTreeAsync(
        ConnectionDescriptor descriptor,
        ICredentialResolver credentials)
    {
        if (ObjectTree is { } existing)
        {
            ObjectTree = null;
            await existing.DisposeAsync().ConfigureAwait(true);
        }

        try
        {
            ICatalogReader catalog = await Task.Run(
                () => _sessionFactory.CreateCatalogReaderAsync(
                    descriptor, credentials, CancellationToken.None)).ConfigureAwait(true);

            ObjectTree = new ObjectTreeViewModel(descriptor, catalog);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The object browser could not be opened.");
            StatusText += " — object browser unavailable: " + ex.Message;
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync(ConnectionItemViewModel? item)
    {
        if (item is null || !_sessions.TryGetValue(item.Descriptor.Id, out IInformixSession? session))
        {
            return;
        }

        session.StateChanged -= OnSessionStateChanged;
        _sessions.Remove(item.Descriptor.Id);
        item.IsConnected = false;

        if (ObjectTree is { } tree && tree.Descriptor.Id == item.Descriptor.Id)
        {
            ObjectTree = null;
            await tree.DisposeAsync().ConfigureAwait(true);
        }

        foreach (EditorTabViewModel tab in Tabs.Where(t => ReferenceEquals(t.Session, session)))
        {
            // The tab and its text survive; only the connection goes.
            tab.Session = null;
        }

        await session.DisposeAsync().ConfigureAwait(true);

        StatusText = $"Disconnected from {item.Descriptor.TargetLabel}";
    }

    /// <summary>Opens an empty editor against the given session.</summary>
    public EditorTabViewModel NewTab(IInformixSession? session = null, string? sql = null, string? title = null)
    {
        var tab = new EditorTabViewModel(_history, (warning, statement) => ConfirmDestructive(warning, statement))
        {
            Session = session ?? SelectedTab?.Session,
            Sql = sql ?? string.Empty,
            Title = title ?? $"Query {_nextTabNumber++}",
        };

        Tabs.Add(tab);
        SelectedTab = tab;

        return tab;
    }

    [RelayCommand]
    public void NewQuery() => NewTab();

    [RelayCommand]
    public async Task CloseTabAsync(EditorTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        Tabs.Remove(tab);
        _autosave.Discard(TabKey(tab));

        await tab.DisposeAsync().ConfigureAwait(true);

        SelectedTab = Tabs.LastOrDefault();
    }

    /// <summary>Restores anything left behind by a run that did not close cleanly (PR-3.9).</summary>
    public int RestoreAutosavedTabs()
    {
        IReadOnlyList<AutosavedTab> recovered = _autosave.Recover();

        foreach (AutosavedTab saved in recovered)
        {
            NewTab(session: null, sql: saved.Sql, title: saved.Title + " (recovered)");
        }

        if (recovered.Count > 0)
        {
            StatusText = $"Recovered {recovered.Count} unsaved tab(s) from the previous session.";
            _logger.LogInformation("Recovered {Count} autosaved tabs.", recovered.Count);
        }

        return recovered.Count;
    }

    /// <summary>Writes every open tab and closes every session. Called at shutdown.</summary>
    public async Task ShutdownAsync()
    {
        _autosaveTimer.Stop();

        if (ObjectTree is { } tree)
        {
            ObjectTree = null;
            await tree.DisposeAsync().ConfigureAwait(true);
        }

        foreach (EditorTabViewModel tab in Tabs)
        {
            // A clean exit still autosaves: reopening where you left off is the
            // behaviour people expect, and PR-3.9 costs nothing extra here.
            _autosave.Save(TabKey(tab), tab.Title, tab.Sql, tab.FilePath);
            await tab.DisposeAsync().ConfigureAwait(true);
        }

        foreach (IInformixSession session in _sessions.Values)
        {
            session.StateChanged -= OnSessionStateChanged;
            await session.DisposeAsync().ConfigureAwait(true);
        }

        _sessions.Clear();
        _history.Trim();
    }

    private void AutosaveDirtyTabs()
    {
        foreach (EditorTabViewModel tab in Tabs.Where(t => t.IsDirty))
        {
            _autosave.Save(TabKey(tab), tab.Title, tab.Sql, tab.FilePath);
            tab.IsDirty = false;
        }
    }

    // Fully qualified: in a WPF file, unqualified Path is System.Windows.Shapes.Path.
    private static string TabKey(EditorTabViewModel tab) =>
        tab.FilePath is null
            ? tab.Title
            : System.IO.Path.GetFileNameWithoutExtension(tab.FilePath) + "-" + tab.Title;

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.Current != SessionState.Broken)
        {
            return;
        }

        // PR-1.7: say so clearly, and be explicit that nothing typed has been lost.
        App.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = "The connection was lost. Your editor contents are safe — reconnect to continue.";

            if (sender is IInformixSession broken)
            {
                foreach (ConnectionItemViewModel item in Connections
                             .Where(c => c.Descriptor.Id == broken.Descriptor.Id))
                {
                    item.IsConnected = false;
                }
            }
        });
    }

    private static string BuildErrorText(InformixError error, ConnectionDescriptor descriptor)
    {
        var text = $"Could not connect to {descriptor.TargetLabel}.\n\n{error.ServerMessage}";

        if (error.Explanation is not null)
        {
            text += $"\n\n{error.Explanation}";
        }

        return text + $"\n\nSQLCODE {error.SqlCode}"
               + (error.IsamCode is { } isam ? $", ISAM {isam}" : string.Empty);
    }

    /// <summary>Holds a password for one connect attempt and nothing longer.</summary>
    private sealed class TransientCredentialResolver(string password) : ICredentialResolver
    {
        public Task<string?> GetPasswordAsync(
            ConnectionDescriptor descriptor,
            CancellationToken cancellationToken) => Task.FromResult<string?>(password);
    }
}
