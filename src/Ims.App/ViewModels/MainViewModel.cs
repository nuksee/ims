using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ims.Core.Catalog;
using Ims.Core.Completion;
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

    /// <summary>
    /// What completion knows about the schema (PR-3.2).
    /// </summary>
    /// <remarks>
    /// Never null, so the editor never has to ask whether there is a connection: with
    /// none, completion still offers the Informix language, which is most of what
    /// PR-8.3 is for and all of what someone drafting a script offline needs.
    /// </remarks>
    public ICatalogSnapshot CompletionCatalog { get; private set; } = EmptyCatalogSnapshot.Instance;

    private CatalogCache? _catalogCache;

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

        // Every tab that has no session yet adopts this one. The startup tab and any
        // tab recovered from autosave (PR-3.9) begin unconnected, and a tab that
        // says "Not connected" while the status bar says "Connected" is a
        // contradiction the user has to resolve by hand for no reason.
        //
        // Tabs already bound to another instance keep it — PR-1.6 is explicit that
        // each editor's target must stay unambiguous.
        var adopted = 0;

        foreach (EditorTabViewModel tab in Tabs.Where(t => t.Session is null))
        {
            tab.Session = session;
            adopted++;
        }

        if (adopted == 0)
        {
            NewTab(session);
        }

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
            CompletionCatalog = EmptyCatalogSnapshot.Instance;
            _catalogCache = null;
            await existing.DisposeAsync().ConfigureAwait(true);
        }

        try
        {
            ICatalogReader catalog = await Task.Run(
                () => _sessionFactory.CreateCatalogReaderAsync(
                    descriptor, credentials, CancellationToken.None)).ConfigureAwait(true);

            // One reader, one connection, shared by the tree and by completion. An
            // Informix connection has one cursor, so the two would otherwise close
            // each other's results — and a second session per instance is exactly the
            // cost PR-6.4 asks IMS not to add.
            var shared = new SerializedCatalogReader(catalog);

            ObjectTree = new ObjectTreeViewModel(descriptor, shared);

            _catalogCache = new CatalogCache(shared);
            CompletionCatalog = _catalogCache;

            // Warmed in the background: PR-3.2 needs the object names before the user
            // types one, and nobody expands a tree first. Not awaited — the editor is
            // usable, and completion improves when the answer lands.
            _ = Task.Run(() => _catalogCache.WarmAsync(CancellationToken.None));
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
            CompletionCatalog = EmptyCatalogSnapshot.Instance;
            _catalogCache = null;
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

    /// <summary>
    /// Closes a tab and picks the next selection.
    /// </summary>
    /// <remarks>
    /// Selection only moves if the tab being closed was the selected one, and then it
    /// lands on the neighbour rather than the end of the strip. Closing a tab you were
    /// not looking at used to jump you to the last one, which was tolerable when the
    /// only way to close was a deliberate click on that tab's ✕ and is not now that
    /// middle-click makes closing a background tab a passing gesture.
    /// </remarks>
    [RelayCommand]
    public async Task CloseTabAsync(EditorTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
        bool wasSelected = ReferenceEquals(SelectedTab, tab);

        Tabs.Remove(tab);
        _autosave.Discard(TabKey(tab));

        await tab.DisposeAsync().ConfigureAwait(true);

        if (wasSelected)
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    /// <summary>Reopens the tabs from the previous session (PR-3.9).</summary>
    /// <remarks>
    /// <para>
    /// The restored tab keeps the saved <see cref="AutosavedTab.Id"/> rather than
    /// minting a new one, so it continues to own its file instead of orphaning it and
    /// writing a second. Getting that wrong is what filled the autosave directory with
    /// one extra copy of every tab per launch.
    /// </para>
    /// <para>
    /// The title is restored verbatim too. Appending " (recovered)" renamed the tab on
    /// every run, so a tab reopened six times was titled "(recovered) (recovered)…" and
    /// — because the key was derived from the title — left six files behind. The status
    /// line says what happened; the tab does not need to carry it forever.
    /// </para>
    /// </remarks>
    public int RestoreAutosavedTabs()
    {
        IReadOnlyList<AutosavedTab> recovered = _autosave.Recover();

        foreach (AutosavedTab saved in recovered)
        {
            EditorTabViewModel tab = NewTab(session: null, sql: saved.Sql, title: saved.Title);
            tab.AdoptAutosaveId(saved.Id);
            tab.FilePath = saved.FilePath;
        }

        if (recovered.Count > 0)
        {
            StatusText = $"Reopened {recovered.Count} tab(s) from the previous session.";
            _logger.LogInformation("Reopened {Count} autosaved tabs.", recovered.Count);
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
            CompletionCatalog = EmptyCatalogSnapshot.Instance;
            _catalogCache = null;
            await tree.DisposeAsync().ConfigureAwait(true);
        }

        foreach (EditorTabViewModel tab in Tabs)
        {
            // A clean exit still autosaves: reopening where you left off is the
            // behaviour people expect, and PR-3.9 costs nothing extra here.
            //
            // An empty tab is not worth reopening, though, and saving it was half of
            // why IMS appeared to "recover" work after a session where nothing had
            // been typed — a blank Query 1 came back as a recovered tab, every time.
            // Recover() already skips blank files on the way in; this stops writing
            // them on the way out, so they do not accumulate either.
            if (string.IsNullOrWhiteSpace(tab.Sql))
            {
                _autosave.Discard(TabKey(tab));
            }
            else
            {
                _autosave.Save(TabKey(tab), tab.Title, tab.Sql, tab.FilePath);
            }

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
            // Emptying a tab is an edit like any other, so it has to remove the file
            // rather than leave the last non-empty version behind to be reopened.
            if (string.IsNullOrWhiteSpace(tab.Sql))
            {
                _autosave.Discard(TabKey(tab));
            }
            else
            {
                _autosave.Save(TabKey(tab), tab.Title, tab.Sql, tab.FilePath);
            }

            tab.IsDirty = false;
        }
    }

    // The tab's own identity, not anything derived from its title or file name. Both
    // of those change while the tab lives, and a key that changes makes the autosave
    // store treat one tab as several.
    private static string TabKey(EditorTabViewModel tab) => tab.AutosaveId;

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
