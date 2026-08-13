using FluentAssertions;
using Ims.Core.Monitoring;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// PR-5.3, PR-6.1 and NFR-4 — the states a snapshot has to be able to represent honestly.
/// </summary>
public sealed class SessionSnapshotTests
{
    [Fact]
    public void An_unavailable_snapshot_says_why_and_claims_nothing()
    {
        // PR-6.1: an ordinary account may legitimately be unable to read sysmaster. That is
        // a fact about their privileges to report, not a fault to raise — and the fidelity
        // must not read as "nothing is blocked", which is a claim IMS cannot make here.
        SessionSnapshot snapshot = SessionSnapshot.Unavailable(
            "This account cannot read sysmaster:syssessions.", DateTimeOffset.UnixEpoch);

        snapshot.IsAvailable.Should().BeFalse();
        snapshot.UnavailableReason.Should().Contain("cannot read sysmaster");
        snapshot.Sessions.Should().BeEmpty();
        snapshot.Fidelity.Should().Be(LockWaitFidelity.Unknown);
    }

    [Fact]
    public void A_read_snapshot_is_available()
    {
        SessionSnapshot snapshot = new()
        {
            Sessions = [],
            Waits = [],
            Fidelity = LockWaitFidelity.BlockerIdentified,
            ReadAt = DateTimeOffset.UnixEpoch,
            Queries = [],
        };

        snapshot.IsAvailable.Should().BeTrue();
        snapshot.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void A_capped_list_still_reports_the_true_session_count()
    {
        // The count comes from its own query for exactly this reason. Reporting the capped
        // list length would understate a busy instance at the moment the number mattered.
        SessionSnapshot snapshot = new()
        {
            Sessions = [Session(1), Session(2)],
            Waits = [],
            Fidelity = LockWaitFidelity.BlockerIdentified,
            TotalSessionCount = 780,
            IsCapped = true,
            ReadAt = DateTimeOffset.UnixEpoch,
            Queries = [],
        };

        snapshot.TotalSessionCount.Should().Be(780);
        snapshot.Sessions.Should().HaveCount(2);
        snapshot.IsCapped.Should().BeTrue();
    }

    [Fact]
    public void A_failed_query_stays_in_the_list_with_its_reason()
    {
        // PR-8.2 and NFR-4 together: a section that failed is part of what IMS asked, and
        // hiding the query that did not work leaves the user staring at an empty pane with
        // no way to find out why.
        ServerQuery failed = new(
            "Resources",
            "SELECT FIRST 1 memused FROM sysmaster:sysrstcb WHERE sid = ?",
            "onstat -g ses <sid>",
            ServerQueryOutcome.Failed,
            "42000 -206: The specified table (sysrstcb) is not in the database.");

        failed.Outcome.Should().Be(ServerQueryOutcome.Failed);
        failed.Message.Should().Contain("-206");
        failed.OnstatEquivalent.Should().Be("onstat -g ses <sid>");
    }

    [Fact]
    public void Session_detail_separates_who_blocks_from_who_is_blocked()
    {
        // Both directions matter and they are different questions. "What am I waiting on"
        // is U1's; "who am I holding up" is what makes someone let go of a transaction.
        SessionDetail detail = new()
        {
            Sid = 52,
            LocksHeld = [],
            Waits =
            [
                new LockWaitEdge { WaiterSid = 52, HolderSid = 47 },
                new LockWaitEdge { WaiterSid = 61, HolderSid = 52 },
            ],
            Queries = [],
        };

        detail.Blockers.Should().ContainSingle().Which.HolderSid.Should().Be(47);
        detail.Blocking.Should().ContainSingle().Which.WaiterSid.Should().Be(61);
    }

    [Fact]
    public void A_lock_names_what_it_is_on_when_it_can()
    {
        LockInfo qualified = new()
        {
            OwnerSid = 47,
            DatabaseName = "stores",
            TableName = "orders",
            LockType = "Exclusive",
            RawLockType = "X",
        };

        qualified.Resource.Should().Be("stores:orders");
    }

    [Fact]
    public void A_lock_with_no_database_still_names_the_table()
    {
        // Partial knowledge beats none: PR-8.4 objects to inventing the missing half, not to
        // reporting the half that is known.
        LockInfo partial = new()
        {
            OwnerSid = 47,
            TableName = "orders",
            LockType = "Shared",
            RawLockType = "S",
        };

        partial.Resource.Should().Be("orders");
    }

    [Fact]
    public void Indicators_are_all_absent_until_something_is_read()
    {
        // PR-5.6 is a Should and every figure comes from its own query, so absent has to be
        // representable for each one independently.
        InstanceIndicators none = InstanceIndicators.None;

        none.Mode.Should().BeNull();
        none.Uptime.Should().BeNull();
        none.ReadCachePercent.Should().BeNull();
        none.LastCheckpoint.Should().BeNull();
        none.Queries.Should().BeEmpty();
    }

    private static SessionInfo Session(int sid) => new()
    {
        Sid = sid,
        UserName = "kaveh",
        State = "Running",
        RawState = "0",
    };
}
