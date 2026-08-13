using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ims.Core.Connections;
using Ims.Core.Monitoring;

namespace Ims.App.ViewModels;

/// <summary>
/// The session monitor, as a tab of its own (PR-5.1 to PR-5.7).
/// </summary>
/// <remarks>
/// <para>
/// A tab rather than a pane, for the reason <see cref="ObjectDetailTabViewModel"/> gives and
/// one of its own: PR-5.5 forbids querying a server for a view nobody is watching, and a tab
/// has an unambiguous answer to whether anyone is. Closing it stops the polling outright;
/// selecting away from it suspends. A docked pane would have needed a notion of "closed"
/// invented for it.
/// </para>
/// <para>
/// One monitor per instance. Two would be two sets of queries against one server for the
/// same answer.
/// </para>
/// <para>
/// The decision about <em>when</em> to query is not made here — it is in
/// <see cref="RefreshPolicy"/>, which has no UI and can therefore be tested. This class owns
/// the timer and asks the policy; the timer is a tick source and the policy is the authority.
/// </para>
/// <para>
/// Property names avoid <c>CanExecute</c>, <c>IsExecuting</c>, <c>Session</c>,
/// <c>SelectedResult</c> and the rest of <see cref="EditorTabViewModel"/>'s surface on
/// purpose. The query toolbar binds to <c>SelectedTab.*</c> with <c>FallbackValue=False</c>
/// and relies on the path not resolving on a non-editor tab, so a name shared with an editor
/// would light up Execute or Commit on a monitor.
/// </para>
/// </remarks>
public sealed partial class SessionMonitorTabViewModel : ObservableObject, ITabViewModel
{
    private readonly ISessionMonitor _monitor;
    private readonly RefreshPolicy _policy;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>The list the grid binds to, filtered and sorted through a view (PR-5.4).</summary>
    private readonly ObservableCollection<SessionRowViewModel> _sessions = [];

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private bool _hideSystemSessions = true;

    [ObservableProperty]
    private SessionRowViewModel? _selectedSession;

    [ObservableProperty]
    private SessionDetail? _detail;

    [ObservableProperty]
    private InstanceIndicators? _indicators;

    [ObservableProperty]
    private bool _isReading;

    /// <summary>
    /// Why the read is taking a while, once it has been long enough to need saying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for the first few seconds, because most reads finish inside that and a warning about
    /// a wait that is already over is just noise. It appears only once the wait is long enough
    /// that the user has started wondering — which is the moment a bare spinner stops being
    /// enough and they need to know whether to keep waiting.
    /// </para>
    /// <para>
    /// It names <c>onstat</c> for the same reason PR-8.3 does everywhere else: if IMS is going to
    /// be slow at something the command line is fast at, saying so is more use than hiding it.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private string? _slowReadNotice;

    /// <summary>
    /// How long a read may run before it explains itself.
    /// </summary>
    /// <remarks>
    /// Three seconds: past NFR-1's 200 ms acknowledgement by enough that something is clearly
    /// wrong, and short enough to appear well before the user gives up.
    /// </remarks>
    private static readonly TimeSpan SlowReadThreshold = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True while the selected session's detail is being read (PR-5.2).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsReading"/> rather than shared with it. The two are
    /// independent reads and the list's Refresh must stay usable while a detail read is in
    /// flight — and on this estate the detail read is the slower of the two, because it is the
    /// one that can spend ten seconds reaching its timeout on <c>syslocks</c>.
    /// </remarks>
    [ObservableProperty]
    private bool _isReadingDetail;

    /// <summary>
    /// How long the selected session's detail took to read.
    /// </summary>
    /// <remarks>
    /// Shown separately from the list's timing because it is the cost paid per click, so it is
    /// the one that decides whether browsing the list is pleasant or not.
    /// </remarks>
    [ObservableProperty]
    private string? _detailReadLabel;

    [ObservableProperty]
    private string? _notice;

    private SessionSnapshot? _snapshot;

    /// <summary>Running while a read is in flight, null otherwise. Drives the count-up.</summary>
    private System.Diagnostics.Stopwatch? _reading;

    /// <summary>How long the last completed read took, kept so the label can say so.</summary>
    private TimeSpan? _lastReadDuration;

    public SessionMonitorTabViewModel(
        ConnectionDescriptor descriptor,
        ISessionMonitor monitor,
        RefreshPolicy? policy = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _policy = policy ?? new RefreshPolicy();

        Sessions = CollectionViewSource.GetDefaultView(_sessions);
        Sessions.Filter = Matches;

        // The tick is deliberately finer than the shortest offered interval: the policy
        // decides whether enough time has passed, so the timer only has to ask often
        // enough not to add noticeable lag to the answer.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        _policy.ViewOpened();
    }

    /// <summary>The instance being watched. Fixed for the life of the tab.</summary>
    public ConnectionDescriptor Descriptor { get; }

    /// <summary>The header text.</summary>
    public string Title => $"Sessions — {Descriptor.ServerName}";

    /// <summary>The session list, filtered (PR-5.4).</summary>
    public ICollectionView Sessions { get; }

    /// <summary>What refresh modes the UI offers (PR-5.5).</summary>
    public static IReadOnlyList<TimeSpan> OfferedIntervals => RefreshPolicy.OfferedIntervals;

    /// <summary>True while refreshing automatically.</summary>
    public bool IsPolling => _policy.IsPolling;

    /// <summary>The interval in use, or null in manual mode.</summary>
    public TimeSpan? Interval => _policy.Mode == RefreshMode.Interval ? _policy.Interval : null;

    /// <summary>
    /// When the list was read, in words, so the user knows how old it is (PR-5.5).
    /// </summary>
    /// <remarks>
    /// Manual is the default, so what is on screen may be minutes old. Saying when it was
    /// read is not decoration: a stale session list is exactly as misleading as a wrong one.
    /// </remarks>
    public string ReadAtLabel => _snapshot is { } snap
        ? $"Read at {snap.ReadAt:HH:mm:ss}{Took}"
        : "Not read yet";

    /// <summary>
    /// How long the last read took, appended to the timestamp.
    /// </summary>
    /// <remarks>
    /// Shown because on this estate it is the number that explains the experience: a refresh that
    /// spent fifty seconds reaching a timeout and one that answered in half a second look
    /// identical once they are over, and only one of them means the instance is worth watching
    /// this way. Seconds once past a second — millisecond precision on a fifty-second wait is
    /// noise.
    /// </remarks>
    private string Took =>
        _lastReadDuration is { } d ? $" · took {Describe(d)}" : string.Empty;

    /// <summary>
    /// How long the read in flight has been running, counted up while it runs.
    /// </summary>
    /// <remarks>
    /// A count-up rather than a progress bar, because IMS does not know how far along it is and
    /// cannot even stop the statement — <c>Cancel()</c> does not reach this server. What it can
    /// honestly show is how long the user has been waiting, which is what they need to decide
    /// whether to keep waiting.
    /// </remarks>
    public string ElapsedLabel => _reading is { } running
        ? $"{running.Elapsed.TotalSeconds:N0}s"
        : string.Empty;

    /// <summary>True when there is a detail timing worth showing.</summary>
    public bool HasDetailReadLabel => !string.IsNullOrEmpty(DetailReadLabel);

    /// <summary>How much IMS could establish about blocking (PR-5.3).</summary>
    public LockWaitFidelity Fidelity => _snapshot?.Fidelity ?? LockWaitFidelity.Unknown;

    /// <summary>
    /// What the blocking picture amounts to, in words the user can act on (PR-5.3, NFR-8).
    /// </summary>
    /// <remarks>
    /// Words rather than a colour or an icon, and the wording tracks the fidelity rather
    /// than flattening it. "Contending with" and "blocked by" are different claims, and only
    /// one of them justifies interrupting a colleague.
    /// </remarks>
    public string FidelityLabel => Fidelity switch
    {
        LockWaitFidelity.BlockerIdentified when BlockedCount > 0 =>
            $"{BlockedCount} session(s) blocked",
        LockWaitFidelity.BlockerIdentified => "Nothing blocked",
        LockWaitFidelity.ContentionOnly =>
            "Sessions are contending on the same rows; IMS cannot tell which is waiting",

        // Distinguish "too slow" from "not there", because they point at different remedies:
        // a timeout says use onstat, an absence says this server has nothing to give. Saying
        // "does not expose" for a timeout would be a small lie about the server (PR-8.2), and
        // it would send someone looking for a permission that was never the problem.
        _ when LockWaitsTimedOut =>
            "Reading locks timed out on this instance — use onstat -g lok at the command line",
        _ => "This server does not expose lock waits to IMS",
    };

    /// <summary>
    /// True when the lock read failed for want of time rather than for want of the object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>syslocks</c> is synthesised from shared memory across every lock in the instance, so
    /// on a busy server it can cost more than the monitor's whole budget to materialise —
    /// measured against 14.10 on 2026-08-13, where even a single scan with no join exceeded ten
    /// seconds. That is a fact about the instance, not a defect, and the UI says which.
    /// </para>
    /// <para>
    /// Read from the outcome the reader recorded. The first version matched a SQLSTATE inside the
    /// error message, which was fragile enough to be wrong in practice — the message shape it
    /// expected was not the one that arrived, so a timeout still read as "does not expose".
    /// </para>
    /// </remarks>
    public bool LockWaitsTimedOut =>
        _snapshot?.Queries.Any(q => q.Outcome is ServerQueryOutcome.TimedOut) ?? false;

    /// <summary>How many sessions are waiting on another (PR-5.3).</summary>
    public int BlockedCount => _snapshot?.Waits.Select(w => w.WaiterSid).Distinct().Count() ?? 0;

    /// <summary>Chains of three or more sessions (PR-5.7).</summary>
    public IReadOnlyList<IReadOnlyList<int>> Chains => _snapshot is { } snap
        ? LockWaitChain.Resolve(snap.Waits)
        : [];

    /// <summary>Set when there is a chain worth drawing — PR-5.7 asks for three or more.</summary>
    public bool HasChains => Chains.Count > 0;

    /// <summary>The chains as text, one per line.</summary>
    public string ChainsLabel =>
        string.Join(Environment.NewLine, Chains.Select(c => string.Join(" ← ", c)));

    /// <summary>Every query behind the list, including any that failed (PR-8.2).</summary>
    public IReadOnlyList<ServerQuery> Queries => _snapshot?.Queries ?? [];

    /// <summary>The queries behind the selected session's detail (PR-8.2).</summary>
    public IReadOnlyList<ServerQuery> DetailQueries => Detail?.Queries ?? [];

    /// <summary>
    /// The queries and their <c>onstat</c> equivalents as one readable block (PR-8.2, PR-8.3).
    /// </summary>
    /// <remarks>
    /// Formatted as SQL comments so the whole thing can be pasted into an editor tab and
    /// run — the same reasoning behind the tree's "Show the catalogue query", where the
    /// point is that the user can take it away and use it.
    /// </remarks>
    public string QueryText
    {
        get
        {
            IEnumerable<ServerQuery> all = Queries.Concat(DetailQueries);
            var text = new System.Text.StringBuilder();

            foreach (ServerQuery query in all)
            {
                if (text.Length > 0)
                {
                    text.AppendLine().AppendLine("-- ────────────────────────────").AppendLine();
                }

                text.Append("-- ").AppendLine(query.Purpose);

                if (query.OnstatEquivalent is { Length: > 0 } onstat)
                {
                    text.Append("-- At the command line: ").AppendLine(onstat);
                }

                // Named per outcome, because someone reading this block to decide what to do
                // next needs to know which: a timeout means run it yourself with more patience,
                // a refusal means it will never answer for this account.
                string? note = query.Outcome switch
                {
                    ServerQueryOutcome.Failed => "-- The server refused this: ",
                    ServerQueryOutcome.TimedOut => "-- This timed out on the server: ",
                    ServerQueryOutcome.NotAttempted => "-- IMS did not send this: ",
                    _ => null,
                };

                if (note is not null)
                {
                    text.Append(note).AppendLine(query.Message);
                }

                text.AppendLine(query.Sql);
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>True when there is anything to show at all.</summary>
    public bool HasSessions => !_sessions.IsEmpty();

    /// <summary>
    /// True when the detail pane should ask the user to pick a session.
    /// </summary>
    /// <remarks>
    /// Only when nothing is selected. Once a row is chosen the pane shows the load button
    /// instead, because detail is no longer fetched by selecting — and a prompt saying "select a
    /// session" above a session that is plainly selected reads as a bug.
    /// </remarks>
    public bool ShowSelectSessionPrompt => SelectedSession is null;

    /// <summary>
    /// True when a session is selected but its detail has not been read.
    /// </summary>
    /// <remarks>
    /// The resting state now, rather than a transient one: selecting a row deliberately queries
    /// nothing, so this is what the pane shows until the user asks.
    /// </remarks>
    public bool ShowLoadDetailPrompt =>
        SelectedSession is not null && Detail is null && !IsReadingDetail;

    // ---- Refresh ----------------------------------------------------------------

    /// <summary>
    /// Reads the session list now, because the user asked (PR-5.5).
    /// </summary>
    /// <remarks>
    /// Permitted in either mode: a manual refresh <em>is</em> the user asking, which is what
    /// PR-5.5 allows by definition and what PR-6.2 counts as a documented action.
    /// </remarks>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_policy.CanRefreshOnDemand() || IsReading)
        {
            return;
        }

        await ReadAsync().ConfigureAwait(true);
    }

    /// <summary>Stops refreshing automatically (PR-5.5).</summary>
    [RelayCommand]
    public void SetManual()
    {
        _policy.SetManual();
        _timer.Stop();
        RaiseRefreshState();
    }

    /// <summary>Refreshes at the given interval while this tab is on screen (PR-5.5).</summary>
    [RelayCommand]
    public void SetInterval(TimeSpan interval)
    {
        _policy.SetInterval(interval);

        if (_policy.IsPolling)
        {
            _timer.Start();
        }

        RaiseRefreshState();
    }

    /// <summary>Called when this tab becomes the one on screen.</summary>
    public void Selected()
    {
        _policy.ViewSelected();

        if (_policy.IsPolling)
        {
            _timer.Start();
        }

        RaiseRefreshState();
    }

    /// <summary>
    /// Called when the user selects another tab.
    /// </summary>
    /// <remarks>
    /// The timer stops rather than being left to tick harmlessly. PR-5.5's "never query a
    /// server while the view is closed" is about load on a production instance, and a
    /// monitor polling behind three editor tabs is precisely the load it means.
    /// </remarks>
    public void Deselected()
    {
        _policy.ViewDeselected();
        _timer.Stop();
        RaiseRefreshState();
    }

    /// <summary>
    /// Reads the list and the indicators, once.
    /// </summary>
    /// <remarks>
    /// Sequential, and not worth "fixing" with a <c>WhenAll</c>. Both reads go down the one
    /// connection behind the one semaphore this monitor shares with the object tree, so running
    /// them together would queue them anyway and only make the wait harder to attribute. The
    /// list goes first because it is what the user opened the tab to see.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await ReadAsync(cancellationToken).ConfigureAwait(true);
        await ReadIndicatorsAsync(cancellationToken).ConfigureAwait(true);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_policy.ShouldRefreshNow() && !IsReading)
        {
            _ = ReadAsync();
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, _closing.Token);

        IsReading = true;
        SlowReadNotice = null;
        _reading = System.Diagnostics.Stopwatch.StartNew();

        // Explains itself if it outlasts the threshold, and says nothing if it does not. The
        // timer is discarded either way, so a fast read costs one cancellation.
        using var slow = new CancellationTokenSource();
        _ = AnnounceIfSlowAsync(slow.Token);
        _ = CountUpAsync(slow.Token);

        try
        {
            // Off the dispatcher: ServerCallGuard throws if a round trip is attempted on
            // it, and NFR-1 is a functional requirement rather than a preference.
            SessionSnapshot snapshot = await Task.Run(
                () => _monitor.GetSessionsAsync(linked.Token), linked.Token).ConfigureAwait(true);

            _policy.RecordQuery();
            Apply(snapshot);
        }
        catch (OperationCanceledException)
        {
            // The tab is closing, or the read was superseded. Nothing to report.
        }
        catch (Exception ex)
        {
            Notice = "The session list could not be read: " + ex.Message;
        }
        finally
        {
            await slow.CancelAsync().ConfigureAwait(true);

            // Kept before the stopwatch is dropped, so the label can say how long it took after
            // the fact — which is the number that explains the experience once the wait is over.
            _lastReadDuration = _reading?.Elapsed;
            _reading = null;

            IsReading = false;
            SlowReadNotice = null;

            OnPropertyChanged(nameof(ElapsedLabel));
            OnPropertyChanged(nameof(ReadAtLabel));
        }
    }

    /// <summary>
    /// Raises <see cref="ElapsedLabel"/> once a second while a read is in flight.
    /// </summary>
    /// <remarks>
    /// A count-up is honest in a way a progress bar is not: IMS does not know how far along the
    /// server is, and it cannot stop it either, since <c>Cancel()</c> does not reach it. Once a
    /// second rather than more often — this is a number someone glances at to decide whether to
    /// keep waiting, not an animation.
    /// </remarks>
    private async Task CountUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
                OnPropertyChanged(nameof(ElapsedLabel));
            }
        }
        catch (OperationCanceledException)
        {
            // The read finished, which is the ordinary way out of this loop.
        }
    }

    /// <summary>
    /// Says why a read is slow, once it has been slow long enough to be worth saying.
    /// </summary>
    /// <remarks>
    /// The wording depends on what the last read learned. Once <c>syslocks</c> has timed out IMS
    /// stops asking for it, so the wait shortens on its own — telling the user that is more use
    /// than a spinner, because it says the next refresh will be quicker rather than leaving them
    /// to conclude the feature is broken.
    /// </remarks>
    private async Task AnnounceIfSlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SlowReadThreshold, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SlowReadNotice = LockWaitsTimedOut
            ? "Reading locks has already timed out on this instance, so IMS has stopped asking "
                + "for them — this refresh should be quicker. onstat -g lok reads them directly."
            : "sysmaster is a view over the server's shared memory, so this can be slow on a "
                + "busy instance. IMS caps each query and gives up rather than holding on.";
    }

    private async Task ReadIndicatorsAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, _closing.Token);

        try
        {
            Indicators = await Task.Run(
                () => _monitor.GetInstanceIndicatorsAsync(linked.Token), linked.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // PR-5.6 is a Should. Losing the strip costs a header, not the view.
            Indicators = null;
        }
    }

    private void Apply(SessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        Notice = snapshot.UnavailableReason;

        int? keep = SelectedSession?.Sid;

        _sessions.Clear();

        foreach (SessionInfo session in snapshot.Sessions)
        {
            _sessions.Add(new SessionRowViewModel(
                session,
                LockWaitChain.BlockersOf(session.Sid, snapshot.Waits),
                IsMine(session)));
        }

        Sessions.Refresh();

        // Put the selection back where the user left it. A refresh that moved it would
        // make an interval refresh unusable — the detail pane would jump under them.
        SelectedSession = _sessions.FirstOrDefault(s => s.Sid == keep);

        OnPropertyChanged(nameof(ReadAtLabel));
        OnPropertyChanged(nameof(Fidelity));
        OnPropertyChanged(nameof(FidelityLabel));
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(Chains));
        OnPropertyChanged(nameof(HasChains));
        OnPropertyChanged(nameof(ChainsLabel));
        OnPropertyChanged(nameof(Queries));
        OnPropertyChanged(nameof(QueryText));
        OnPropertyChanged(nameof(HasSessions));
    }

    /// <summary>
    /// Whether a session belongs to the user IMS is connected as (PR-5.4).
    /// </summary>
    /// <remarks>
    /// Matched on user name rather than on the session IMS itself opened, because U1's
    /// question is "is my work blocked" and their work may be in three sessions. Without a
    /// known user name nothing is marked: a wrong "you" is worse than no marker.
    /// </remarks>
    private bool IsMine(SessionInfo session) =>
        Descriptor.UserName is { Length: > 0 } me
        && string.Equals(session.UserName, me, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Filters the list client-side (PR-5.4).
    /// </summary>
    /// <remarks>
    /// Client-side, unlike the object tree's filter, and for the opposite reason. The tree
    /// filters in the query because with 20,000 objects the list is what it is trying not to
    /// fetch; a session list is tens of rows, and re-querying <c>sysmaster</c> on every
    /// keystroke would be exactly the load PR-6.4 and PR-5.5 rule out.
    /// </remarks>
    private bool Matches(object item)
    {
        if (item is not SessionRowViewModel row)
        {
            return false;
        }

        if (HideSystemSessions && row.IsSystem)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Filter))
        {
            return true;
        }

        string needle = Filter.Trim();

        return Contains(row.UserName, needle)
            || Contains(row.HostName, needle)
            || Contains(row.Application, needle)
            || Contains(row.State, needle)
            || Contains(row.Sid.ToString(System.Globalization.CultureInfo.InvariantCulture), needle);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    partial void OnFilterChanged(string? value) => Sessions.Refresh();

    partial void OnHideSystemSessionsChanged(bool value) => Sessions.Refresh();

    // ShowSelectSessionPrompt reads all three of these, and derived properties in this app are
    // raised by hand rather than through [NotifyPropertyChangedFor].
    partial void OnDetailChanged(SessionDetail? value) =>
        OnPropertyChanged(nameof(ShowSelectSessionPrompt));

    partial void OnIsReadingDetailChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSelectSessionPrompt));
        OnPropertyChanged(nameof(CanLoadDetail));
    }

    partial void OnDetailReadLabelChanged(string? value) =>
        OnPropertyChanged(nameof(HasDetailReadLabel));

    /// <summary>
    /// Clears the detail when the selection moves. Reads nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selecting a session used to read its detail immediately, which made moving down the list
    /// with the arrow keys issue three queries per row. That is the opposite of what PR-5.5 asks
    /// for — refresh "only on explicit action" — and the list itself already worked that way while
    /// the detail pane quietly did not.
    /// </para>
    /// <para>
    /// It is worse than untidy on a server like this one, where all three of those queries fail:
    /// the reads cost a round trip each to return nothing, and a keypress is not an explicit
    /// request for three server queries. <see cref="LoadDetailCommand"/> is.
    /// </para>
    /// </remarks>
    partial void OnSelectedSessionChanged(SessionRowViewModel? value)
    {
        Detail = null;
        DetailReadLabel = null;
        IsReadingDetail = false;

        OnPropertyChanged(nameof(DetailQueries));
        OnPropertyChanged(nameof(QueryText));
        OnPropertyChanged(nameof(ShowSelectSessionPrompt));
        OnPropertyChanged(nameof(CanLoadDetail));
        OnPropertyChanged(nameof(LoadDetailLabel));
    }

    /// <summary>
    /// Reads the selected session's detail, because the user asked (PR-5.2, PR-5.5).
    /// </summary>
    /// <remarks>
    /// The documented action PR-6.2 requires before IMS sends a statement nobody typed. Selecting
    /// a row is navigation; pressing this is a request.
    /// </remarks>
    [RelayCommand]
    public async Task LoadDetailAsync()
    {
        if (SelectedSession is not { } row || IsReadingDetail)
        {
            return;
        }

        await ReadDetailAsync(row.Sid).ConfigureAwait(true);
    }

    /// <summary>True when there is a session selected to load detail for.</summary>
    public bool CanLoadDetail => SelectedSession is not null && !IsReadingDetail;

    /// <summary>Names the session on the button, so it is clear what will be queried.</summary>
    public string LoadDetailLabel => SelectedSession is { } row
        ? $"Load detail for session {row.Sid}"
        : "Load detail";

    private async Task ReadDetailAsync(int sid)
    {
        IsReadingDetail = true;

        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // The snapshot's waits go in rather than being re-read. Without this, clicking
            // through the list re-ran the lock-wait query once per session — ten seconds each
            // against 14.10, every one of them holding the shared connection.
            IReadOnlyList<LockWaitEdge> waits = _snapshot?.Waits ?? [];

            SessionDetail detail = await Task.Run(
                () => _monitor.GetSessionDetailAsync(sid, waits, _closing.Token), _closing.Token)
                .ConfigureAwait(true);

            // The selection may have moved on while this was in flight. Showing one
            // session's locks under another's heading would be worse than showing none.
            if (SelectedSession?.Sid == sid)
            {
                Detail = detail;
                OnPropertyChanged(nameof(DetailQueries));
                OnPropertyChanged(nameof(QueryText));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Notice = $"Session {sid} detail could not be read: " + ex.Message;
        }
        finally
        {
            // Always cleared, unlike when this ran on selection: only one read can be in flight
            // now, because the command will not start a second, so there is no later read whose
            // indicator this could wrongly clear. Leaving it set when the selection has moved on
            // would strand the pane in a reading state nothing was going to leave.
            IsReadingDetail = false;

            // The timing belongs to the session it measured. If the user has moved on, it would
            // read as this row's cost, which it is not.
            if (SelectedSession?.Sid == sid)
            {
                DetailReadLabel = Describe(elapsed.Elapsed);
            }
        }
    }

    /// <summary>A duration in the units it deserves.</summary>
    /// <remarks>
    /// Millisecond precision on a fifty-second wait is noise, and second precision on a
    /// half-second read says "0 s", which reads as broken.
    /// </remarks>
    private static string Describe(TimeSpan duration) => duration.TotalSeconds < 1
        ? $"{duration.TotalMilliseconds:N0} ms"
        : $"{duration.TotalSeconds:N1} s";

    private void RaiseRefreshState()
    {
        OnPropertyChanged(nameof(IsPolling));
        OnPropertyChanged(nameof(Interval));
    }

    /// <summary>
    /// Stops everything. Nothing queries after this (PR-5.5).
    /// </summary>
    /// <remarks>
    /// Reached through <c>MainViewModel.CloseTabAsync</c>, which already awaits disposal, so
    /// closing the tab is enough to end the polling with no special case in the close path.
    /// The policy's closed state is terminal, so a read already in flight cannot schedule
    /// another.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _policy.ViewClosed();
        _timer.Stop();
        _timer.Tick -= OnTick;

        await _closing.CancelAsync().ConfigureAwait(true);
        _closing.Dispose();
    }
}

/// <summary>
/// One row of the session list (PR-5.1, PR-5.3, PR-5.4).
/// </summary>
/// <remarks>
/// Wraps <see cref="SessionInfo"/> rather than being bound to directly, because the grid
/// needs two things the record has no business knowing: whether this session is the user's,
/// and who is blocking it. Both are relationships rather than properties of the session.
/// </remarks>
public sealed class SessionRowViewModel(SessionInfo session, IReadOnlyList<int> blockers, bool isMine)
{
    public SessionInfo Session { get; } = session;

    public int Sid => Session.Sid;

    public string UserName => Session.UserName;

    public string? HostName => Session.HostName;

    public string? Application => Session.Application;

    public string State => Session.State;

    public DateTimeOffset? ConnectedAt => Session.ConnectedAt;

    public bool IsSystem => Session.IsSystem;

    /// <summary>True when this session belongs to the connected user (PR-5.4).</summary>
    public bool IsMine { get; } = isMine;

    /// <summary>
    /// A word, not a colour (NFR-8).
    /// </summary>
    /// <remarks>
    /// The row may also be tinted, but the tint is decoration. This follows the environment
    /// indicator's precedent, where PR-1.5's meaning is carried by the word PRODUCTION and
    /// the colour is strictly secondary.
    /// </remarks>
    public string MineLabel => IsMine ? "YOU" : string.Empty;

    /// <summary>Sessions this one is waiting on (PR-5.3).</summary>
    public IReadOnlyList<int> Blockers { get; } = blockers;

    public bool IsBlocked => Blockers.Count > 0;

    /// <summary>
    /// Who is holding this session up, named (PR-5.3).
    /// </summary>
    /// <remarks>
    /// Empty rather than "none" when nothing blocks it, so the column reads as a list of
    /// problems rather than a column of noise.
    /// </remarks>
    public string BlockedByLabel => Blockers.Count switch
    {
        0 => string.Empty,
        1 => $"← {Blockers[0]}",
        _ => "← " + string.Join(", ", Blockers),
    };
}

/// <summary>Small helpers the view model would otherwise repeat.</summary>
internal static class SessionMonitorExtensions
{
    /// <summary>True when a collection has nothing in it.</summary>
    public static bool IsEmpty<T>(this ObservableCollection<T> collection) => collection.Count == 0;
}
