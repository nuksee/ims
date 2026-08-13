using FluentAssertions;
using Ims.Core.Monitoring;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// PR-5.5 — manual by default, a user-chosen interval, and never a query for a view nobody
/// is watching.
/// </summary>
/// <remarks>
/// The policy exists as a separate type precisely so these can be asserted. Left inside the
/// view model, the third clause would have been a timer callback nobody could test, and it
/// is the clause most easily half-implemented: a DispatcherTimer left running on a hidden
/// tab keeps a production instance answering questions nobody reads (PR-6.4).
/// </remarks>
public sealed class RefreshPolicyTests
{
    private static RefreshPolicy Watching(Func<DateTimeOffset> clock)
    {
        var policy = new RefreshPolicy(clock);
        policy.ViewOpened();
        return policy;
    }

    [Fact]
    public void Starts_manual()
    {
        // PR-5.5 says "defaulting to manual", and PR-6.2 says IMS sends nothing the user
        // did not ask for. Manual is where those meet, so it is the initial state.
        var policy = new RefreshPolicy();

        policy.Mode.Should().Be(RefreshMode.Manual);
        policy.IsPolling.Should().BeFalse();
    }

    [Fact]
    public void Does_not_poll_before_the_view_is_open()
    {
        var policy = new RefreshPolicy();
        policy.SetInterval(TimeSpan.FromSeconds(5));

        policy.ShouldRefreshNow().Should().BeFalse(
            "because nothing may query for a view that has not opened yet");
    }

    [Fact]
    public void Polls_once_open_and_set_to_an_interval()
    {
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);
        policy.SetInterval(TimeSpan.FromSeconds(5));

        // The first tick refreshes immediately: the user has just asked for live data, and
        // waiting a full interval to give them any would read as the setting not working.
        policy.IsPolling.Should().BeTrue();
        policy.ShouldRefreshNow().Should().BeTrue();
    }

    [Fact]
    public void Waits_out_the_interval_between_reads()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        RefreshPolicy policy = Watching(() => now);
        policy.SetInterval(TimeSpan.FromSeconds(30));

        policy.RecordQuery();

        policy.ShouldRefreshNow().Should().BeFalse("because no time has passed");

        now += TimeSpan.FromSeconds(29);
        policy.ShouldRefreshNow().Should().BeFalse("because the interval has not elapsed");

        now += TimeSpan.FromSeconds(1);
        policy.ShouldRefreshNow().Should().BeTrue();
    }

    [Fact]
    public void Stops_polling_while_another_tab_is_selected()
    {
        // The heart of PR-5.5. A monitor sitting behind three editor tabs polling every
        // five seconds is the load PR-6.4 asks IMS not to add, and the user is not even
        // looking at the answer.
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);
        policy.SetInterval(TimeSpan.FromSeconds(5));

        policy.ViewDeselected();

        policy.IsPolling.Should().BeFalse();
        policy.ShouldRefreshNow().Should().BeFalse();

        policy.ViewSelected();
        policy.ShouldRefreshNow().Should().BeTrue("because the user is looking at it again");
    }

    [Fact]
    public void Never_queries_once_the_view_is_closed()
    {
        // PR-5.5 names this outright: never query a server while the view is closed. A read
        // already in flight when the tab closes must not schedule another.
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);
        policy.SetInterval(TimeSpan.FromSeconds(5));

        policy.ViewClosed();

        policy.IsPolling.Should().BeFalse();
        policy.ShouldRefreshNow().Should().BeFalse();
        policy.CanRefreshOnDemand().Should().BeFalse(
            "because a request arriving after the tab has gone has nobody to show");
    }

    [Fact]
    public void Closing_is_terminal()
    {
        // Reopening the monitor builds a new policy, so there is nothing here worth
        // resurrecting — and allowing it would leave a route by which a closed view queries.
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);
        policy.ViewClosed();

        policy.ViewOpened();
        policy.ViewSelected();
        policy.SetInterval(TimeSpan.FromSeconds(5));

        policy.IsViewOpen.Should().BeFalse();
        policy.ShouldRefreshNow().Should().BeFalse();
    }

    [Fact]
    public void Allows_a_manual_refresh_in_manual_mode()
    {
        // A manual refresh is the user asking, which PR-5.5 permits by definition and
        // PR-6.2 counts as a documented action.
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);

        policy.Mode.Should().Be(RefreshMode.Manual);
        policy.CanRefreshOnDemand().Should().BeTrue();
        policy.ShouldRefreshNow().Should().BeFalse("because the timer must not act in manual mode");
    }

    [Fact]
    public void Allows_a_manual_refresh_even_when_not_selected()
    {
        // The gesture can only come from a selected tab anyway, and refusing it would only
        // ever surprise someone.
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);
        policy.ViewDeselected();

        policy.CanRefreshOnDemand().Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void Clamps_an_interval_below_the_minimum(int seconds)
    {
        // PR-6.4 wants these queries negligible on a production instance. A one-second
        // interval left running all afternoon is not, and the clamp means no caller can ask
        // for one.
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);

        policy.SetInterval(TimeSpan.FromSeconds(seconds));

        policy.Interval.Should().Be(RefreshPolicy.MinimumInterval);
    }

    [Fact]
    public void Keeps_an_interval_at_or_above_the_minimum()
    {
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);

        policy.SetInterval(TimeSpan.FromMinutes(5));

        policy.Interval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Going_back_to_manual_stops_the_polling()
    {
        RefreshPolicy policy = Watching(() => DateTimeOffset.UnixEpoch);
        policy.SetInterval(TimeSpan.FromSeconds(5));

        policy.SetManual();

        policy.IsPolling.Should().BeFalse();
        policy.ShouldRefreshNow().Should().BeFalse();
    }

    [Fact]
    public void Offers_only_intervals_at_or_above_the_minimum()
    {
        RefreshPolicy.OfferedIntervals.Should().NotBeEmpty();
        RefreshPolicy.OfferedIntervals.Should().OnlyContain(
            i => i >= RefreshPolicy.MinimumInterval,
            "because offering an interval the policy would clamp would be a lie in the UI");
    }
}
