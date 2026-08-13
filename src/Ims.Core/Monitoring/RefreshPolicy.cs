namespace Ims.Core.Monitoring;

/// <summary>How often the session monitor may query, if at all (PR-5.5).</summary>
public enum RefreshMode
{
    /// <summary>
    /// The default. IMS queries only when the user asks.
    /// </summary>
    /// <remarks>
    /// PR-5.5 says "defaulting to manual" and PR-6.2 says IMS sends no statement the user
    /// did not request. Manual is where those two meet, so it is the initial state rather
    /// than a setting to be found.
    /// </remarks>
    Manual,

    /// <summary>At the user's chosen interval, and only while the view is watching.</summary>
    Interval,
}

/// <summary>
/// Decides whether the session monitor may query the server right now (PR-5.5).
/// </summary>
/// <remarks>
/// <para>
/// PR-5.5 has three parts and they are easy to half-implement: manual by default, a
/// user-chosen interval, and <em>never query a server while the view is closed</em>. The
/// last is the one a timer gets wrong. A <c>DispatcherTimer</c> left running on a tab
/// nobody is looking at keeps a production instance answering questions nobody reads,
/// which is precisely the load PR-6.4 asks IMS not to add.
/// </para>
/// <para>
/// So the decision is a pure function of state and lives here, where it is tested, rather
/// than spread across a timer callback and a visibility handler. The view model keeps the
/// timer; the timer only ever asks this.
/// </para>
/// <para>
/// Deselecting the tab suspends as surely as closing it. An unwatched refresh is unwatched
/// either way, and a monitor sitting behind three editor tabs polling every five seconds
/// would be the worst version of this feature.
/// </para>
/// <para>
/// Not thread-safe, and does not need to be: it is driven from the UI thread and makes no
/// server call itself. It only ever answers a question.
/// </para>
/// </remarks>
public sealed class RefreshPolicy
{
    /// <summary>
    /// The shortest interval offered.
    /// </summary>
    /// <remarks>
    /// PR-6.4 wants these queries negligible on a production instance. Five seconds is
    /// frequent enough to watch a lock clear and slow enough that the cost stays in the
    /// noise; a one-second option would invite someone to leave it running all afternoon.
    /// </remarks>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);

    /// <summary>The intervals the UI offers, so the choice is bounded (PR-5.5, PR-6.4).</summary>
    public static readonly IReadOnlyList<TimeSpan> OfferedIntervals =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
    ];

    private readonly Func<DateTimeOffset> _now;
    private bool _closed;

    /// <param name="now">
    /// The clock. Injected so tests decide what time it is instead of waiting for it.
    /// </param>
    public RefreshPolicy(Func<DateTimeOffset>? now = null) =>
        _now = now ?? (() => DateTimeOffset.Now);

    public RefreshMode Mode { get; private set; } = RefreshMode.Manual;

    public TimeSpan Interval { get; private set; } = TimeSpan.FromSeconds(30);

    /// <summary>True between <see cref="ViewOpened"/> and <see cref="ViewClosed"/>.</summary>
    public bool IsViewOpen { get; private set; }

    /// <summary>True while the monitor's tab is the one on screen.</summary>
    public bool IsViewSelected { get; private set; }

    public DateTimeOffset? LastQueriedAt { get; private set; }

    /// <summary>True only while the view is open, selected, and set to poll.</summary>
    public bool IsPolling => IsViewOpen && IsViewSelected && Mode == RefreshMode.Interval;

    public void ViewOpened()
    {
        if (_closed)
        {
            return;
        }

        IsViewOpen = true;
        IsViewSelected = true;
    }

    /// <summary>
    /// The view has gone. Nothing may query afterwards.
    /// </summary>
    /// <remarks>
    /// Terminal, deliberately. A refresh already in flight when the tab closes must not
    /// issue another, and reopening the monitor builds a new policy with a new timer — so
    /// there is no state worth resurrecting here, and allowing it would leave a way for a
    /// closed view to query. That is the one thing PR-5.5 names outright.
    /// </remarks>
    public void ViewClosed()
    {
        _closed = true;
        IsViewOpen = false;
        IsViewSelected = false;
    }

    public void ViewSelected()
    {
        if (!_closed && IsViewOpen)
        {
            IsViewSelected = true;
        }
    }

    public void ViewDeselected() => IsViewSelected = false;

    public void SetManual() => Mode = RefreshMode.Manual;

    /// <summary>Switches to interval refresh, clamped to <see cref="MinimumInterval"/>.</summary>
    public void SetInterval(TimeSpan interval)
    {
        Mode = RefreshMode.Interval;
        Interval = interval < MinimumInterval ? MinimumInterval : interval;
    }

    /// <summary>Records that a read happened, whatever asked for it.</summary>
    public void RecordQuery() => LastQueriedAt = _now();

    /// <summary>
    /// May the timer query now?
    /// </summary>
    /// <remarks>
    /// Never true when the view is not watching, whatever the timer thinks. The timer is a
    /// tick source and this is the authority.
    /// </remarks>
    public bool ShouldRefreshNow()
    {
        if (!IsPolling)
        {
            return false;
        }

        // The first tick after switching to an interval refreshes immediately: the user
        // has just asked for live data and waiting a full interval to give them any would
        // read as the setting not having worked.
        return LastQueriedAt is not { } last || _now() - last >= Interval;
    }

    /// <summary>
    /// May an explicit user action query now?
    /// </summary>
    /// <remarks>
    /// True whenever the view is open, whatever the mode — a manual refresh <em>is</em> the
    /// user asking, which is what PR-5.5 permits by definition, and it does not require
    /// the tab to be selected because the action can only come from a selected tab anyway.
    /// False once closed, because a request arriving after the tab has gone has nobody to
    /// show.
    /// </remarks>
    public bool CanRefreshOnDemand() => IsViewOpen && !_closed;
}
