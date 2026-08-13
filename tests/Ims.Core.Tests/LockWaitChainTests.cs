using FluentAssertions;
using Ims.Core.Monitoring;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// PR-5.3 and PR-5.7 — who is blocking whom, and the chain when more than two sessions are.
/// </summary>
/// <remarks>
/// These are the cases a live server will not produce on demand. A three-deep lock chain
/// needs three cooperating sessions and a real deadlock cannot be requested at all — and
/// DEP-2 means there is no instance where arranging even a two-session block is safe, because
/// the test database shares a server with production. So the algorithm is pure and lives in
/// Ims.Core, and this is where its correctness is established.
/// </remarks>
public sealed class LockWaitChainTests
{
    private static LockWaitEdge Waits(int waiter, int holder) => new()
    {
        WaiterSid = waiter,
        HolderSid = holder,
    };

    [Fact]
    public void Names_the_single_session_blocking_another()
    {
        // PR-5.3's core: identify the blocked session and the session blocking it.
        LockWaitEdge[] edges = [Waits(52, 47)];

        LockWaitChain.BlockerOf(52, edges).Should().Be(47);
        LockWaitChain.BlockerOf(47, edges).Should().BeNull("because nothing blocks the holder");
    }

    [Fact]
    public void Declines_to_name_one_blocker_when_there_are_several()
    {
        // "Blocked by 47" and "blocked by 47 and two others" are different facts, and naming
        // only the first would be the more confident of the two while being the less true.
        LockWaitEdge[] edges = [Waits(52, 47), Waits(52, 61)];

        LockWaitChain.BlockerOf(52, edges).Should().BeNull();
        LockWaitChain.BlockersOf(52, edges).Should().Equal(47, 61);
    }

    [Fact]
    public void Ignores_a_session_recorded_as_blocking_itself()
    {
        // A session does not block itself, so an edge saying so is a mis-join — most likely
        // syslocks.owner being a process id rather than a session id, which is the failure
        // mode the whole fidelity grading exists for.
        LockWaitEdge[] edges = [Waits(47, 47)];

        LockWaitChain.BlockerOf(47, edges).Should().BeNull();
        LockWaitChain.Resolve(edges).Should().BeEmpty();
    }

    [Fact]
    public void Does_not_draw_a_chain_for_a_bare_pair()
    {
        // PR-5.7 asks for a chain "where more than two sessions are involved". The list
        // already shows A blocked by B; a two-node diagram beside it adds ceremony, not
        // information.
        LockWaitEdge[] edges = [Waits(52, 47)];

        LockWaitChain.Resolve(edges).Should().BeEmpty();
    }

    [Fact]
    public void Resolves_a_three_session_chain_from_the_holder_outwards()
    {
        // 61 waits on 52, which waits on 47. 47 is the one actually holding everyone up, so
        // it heads the chain: the point of the view is that the head is who to talk to.
        LockWaitEdge[] edges = [Waits(52, 47), Waits(61, 52)];

        IReadOnlyList<IReadOnlyList<int>> chains = LockWaitChain.Resolve(edges);

        chains.Should().HaveCount(1);
        chains[0].Should().Equal(47, 52, 61);
    }

    [Fact]
    public void Resolves_a_chain_that_forks()
    {
        // Two sessions waiting on one that is itself waiting: two paths, both worth showing,
        // because either waiter's owner needs to know where the queue ends.
        LockWaitEdge[] edges = [Waits(52, 47), Waits(61, 52), Waits(62, 52)];

        IReadOnlyList<IReadOnlyList<int>> chains = LockWaitChain.Resolve(edges);

        chains.Should().HaveCount(2);
        chains.Should().ContainEquivalentOf(new[] { 47, 52, 61 });
        chains.Should().ContainEquivalentOf(new[] { 47, 52, 62 });
    }

    [Fact]
    public void Terminates_on_a_cycle_and_reports_it_as_one()
    {
        // A real deadlock is exactly a cycle, and the server may not have detected it when
        // IMS read the locks — so this is a state the resolver will genuinely be handed. It
        // must terminate; a monitor that hangs on a deadlock is worse than no monitor.
        LockWaitEdge[] edges = [Waits(47, 52), Waits(52, 61), Waits(61, 47)];

        IReadOnlyList<IReadOnlyList<int>> chains = LockWaitChain.Resolve(edges);

        chains.Should().HaveCount(1);
        chains[0].Should().HaveCount(3);
        chains[0].Should().BeEquivalentTo(new[] { 47, 52, 61 });
    }

    [Fact]
    public void Reports_a_cycle_from_a_stable_starting_point()
    {
        // Otherwise the same deadlock renders differently on each refresh, and a user
        // watching it would think the situation was changing when it was not.
        LockWaitEdge[] forwards = [Waits(47, 52), Waits(52, 61), Waits(61, 47)];
        LockWaitEdge[] shuffled = [Waits(61, 47), Waits(47, 52), Waits(52, 61)];

        LockWaitChain.Resolve(forwards)[0].Should().Equal(LockWaitChain.Resolve(shuffled)[0]);
    }

    [Fact]
    public void Does_not_hang_on_a_two_session_cycle()
    {
        // The tightest deadlock there is: each holds what the other wants.
        LockWaitEdge[] edges = [Waits(47, 52), Waits(52, 47)];

        // Fewer than three sessions, so PR-5.7 draws nothing — but it must still return.
        LockWaitChain.Resolve(edges).Should().BeEmpty();
        LockWaitChain.BlockerOf(47, edges).Should().Be(52);
        LockWaitChain.BlockerOf(52, edges).Should().Be(47);
    }

    [Fact]
    public void Handles_a_chain_that_leads_into_a_cycle()
    {
        // 70 waits on a deadlocked pair. It is stuck behind something that will not resolve
        // on its own, which is worth surfacing rather than dropping.
        LockWaitEdge[] edges = [Waits(47, 52), Waits(52, 47), Waits(70, 47)];

        IReadOnlyList<IReadOnlyList<int>> chains = LockWaitChain.Resolve(edges);

        chains.Should().NotBeEmpty();
        chains.SelectMany(c => c).Should().Contain(70);
    }

    [Fact]
    public void Survives_an_edge_naming_a_session_that_has_gone()
    {
        // The list and the locks are two reads, so a session can end between them. That is
        // ordinary, not exceptional, and it must not throw.
        LockWaitEdge[] edges = [Waits(52, 999), Waits(61, 52)];

        Action act = () => LockWaitChain.Resolve(edges);

        act.Should().NotThrow();
        LockWaitChain.Resolve(edges)[0].Should().Equal(999, 52, 61);
    }

    [Fact]
    public void Handles_no_edges_at_all()
    {
        // The ordinary state of a healthy instance.
        LockWaitChain.Resolve([]).Should().BeEmpty();
        LockWaitChain.BlockerOf(47, []).Should().BeNull();
        LockWaitChain.BlockersOf(47, []).Should().BeEmpty();
    }

    [Fact]
    public void Ignores_a_duplicated_edge()
    {
        // syslocks can report the same contention on several rows of the same table, so the
        // same pair arrives more than once. It is one relationship.
        LockWaitEdge[] edges = [Waits(52, 47), Waits(52, 47), Waits(61, 52)];

        LockWaitChain.BlockersOf(52, edges).Should().Equal(47);
        LockWaitChain.Resolve(edges).Should().HaveCount(1);
    }

    [Fact]
    public void Puts_the_longest_chain_first()
    {
        // The longest chain is the worst problem, and it is what someone opening the monitor
        // during an incident needs to see without scrolling.
        LockWaitEdge[] edges =
        [
            Waits(2, 1), Waits(3, 2), Waits(4, 3),
            Waits(20, 10), Waits(30, 20),
        ];

        IReadOnlyList<IReadOnlyList<int>> chains = LockWaitChain.Resolve(edges);

        chains.Should().HaveCount(2);
        chains[0].Should().Equal(1, 2, 3, 4);
        chains[1].Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Rejects_a_null_edge_list()
    {
        Action act = () => LockWaitChain.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
