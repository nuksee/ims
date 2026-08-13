namespace Ims.Core.Monitoring;

/// <summary>
/// Turns waiter-to-holder pairs into the dependency chains PR-5.7 asks for.
/// </summary>
/// <remarks>
/// <para>
/// Pure, static, and in <c>Ims.Core</c> rather than beside the reader that produces the
/// edges. Two reasons, and the second is the load-bearing one: it needs no server, and
/// <c>Ims.Core.Tests</c> can therefore exercise every case — including the ones a live
/// instance will not reliably produce on demand. A deadlock is not something you can ask
/// a production server for.
/// </para>
/// <para>
/// Cycle safety is a correctness requirement here, not a defensive nicety. A real
/// deadlock is exactly a cycle, and the server may not have detected it yet when IMS
/// reads the locks — so A waiting on B waiting on A is a state this will genuinely be
/// handed. Every walk therefore carries a visited set and stops on a repeat.
/// </para>
/// </remarks>
public static class LockWaitChain
{
    /// <summary>
    /// The session directly blocking this one, where exactly one does.
    /// </summary>
    /// <remarks>
    /// Null when nothing blocks it, and also null when several sessions do — because
    /// "blocked by 4821" and "blocked by 4821 and two others" are different facts, and
    /// naming only the first would be the more confident of the two while being the less
    /// true. The caller asks <see cref="BlockersOf"/> when it wants them all.
    /// </remarks>
    public static int? BlockerOf(int sid, IEnumerable<LockWaitEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        int[] holders = BlockersOf(sid, edges).ToArray();
        return holders.Length == 1 ? holders[0] : null;
    }

    /// <summary>Every session directly blocking this one, in ascending order.</summary>
    public static IReadOnlyList<int> BlockersOf(int sid, IEnumerable<LockWaitEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        return edges
            .Where(e => e.WaiterSid == sid && e.HolderSid != sid)
            .Select(e => e.HolderSid)
            .Distinct()
            .Order()
            .ToArray();
    }

    /// <summary>
    /// Chains of three or more sessions, longest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PR-5.7 asks for a chain "where more than two sessions are involved", so a bare
    /// pair is not returned: the session list already shows A blocked by B, and drawing a
    /// two-node chain beside it would add ceremony rather than information.
    /// </para>
    /// <para>
    /// Each chain starts at a session nothing waits on — the far end of the queue, the one
    /// actually holding everyone else up — and follows the waits back. That ordering is
    /// the point of the view: the head of the returned chain is who to talk to.
    /// </para>
    /// <para>
    /// A cycle has no such head, so it is reported as the loop it is, starting from its
    /// lowest session id to keep the output stable between refreshes. Without a fixed
    /// starting point the same deadlock would render differently each time it was read.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<int>> Resolve(IEnumerable<LockWaitEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        // waiter -> holders. Self-edges are dropped: a session does not block itself, and
        // one in the data would be a mis-join rather than a wait.
        Dictionary<int, List<int>> waitsOn = [];
        HashSet<int> everyone = [];

        foreach (LockWaitEdge edge in edges)
        {
            everyone.Add(edge.WaiterSid);
            everyone.Add(edge.HolderSid);

            if (edge.WaiterSid == edge.HolderSid)
            {
                continue;
            }

            if (!waitsOn.TryGetValue(edge.WaiterSid, out List<int>? holders))
            {
                holders = [];
                waitsOn[edge.WaiterSid] = holders;
            }

            if (!holders.Contains(edge.HolderSid))
            {
                holders.Add(edge.HolderSid);
            }
        }

        // holder -> the sessions waiting on it, which is the direction a chain reads.
        // Built once here rather than per root: with one root per held resource, building
        // it inside the walk would be quadratic in the edge count for no gain.
        Dictionary<int, List<int>> waitedOnBy = [];
        foreach ((int waiter, List<int> holders) in waitsOn)
        {
            foreach (int holder in holders)
            {
                if (!waitedOnBy.TryGetValue(holder, out List<int>? waiters))
                {
                    waiters = [];
                    waitedOnBy[holder] = waiters;
                }

                waiters.Add(waiter);
            }
        }

        List<IReadOnlyList<int>> chains = [];
        HashSet<int> claimed = [];

        // Start from the sessions that are waiting for nothing: they are the roots of the
        // wait forest, and walking from a middle node would report the same chain twice.
        foreach (int root in everyone.Where(s => !waitsOn.ContainsKey(s)).Order())
        {
            foreach (IReadOnlyList<int> chain in WalkFrom(root, waitedOnBy))
            {
                if (chain.Count > 2)
                {
                    chains.Add(chain);
                    foreach (int sid in chain)
                    {
                        claimed.Add(sid);
                    }
                }
            }
        }

        // Whatever is left is in or behind a cycle — every session there waits on something,
        // so it has no root and the walk above never reached it. Each distinct loop is
        // reported once: reported per member, a three-way deadlock would appear three times
        // as the same three sessions rotated, which reads as three problems.
        HashSet<int> inACycle = [];

        foreach (int start in everyone.Where(s => waitsOn.ContainsKey(s)).Order())
        {
            if (inACycle.Contains(start))
            {
                continue;
            }

            if (WalkCycle(start, waitsOn) is not { Count: > 0 } cycle)
            {
                continue;
            }

            foreach (int sid in cycle)
            {
                inACycle.Add(sid);
            }

            if (cycle.Count > 2)
            {
                chains.Add(cycle);
            }
        }

        // A session waiting on a deadlocked pair is stuck behind something that will not
        // clear on its own, which is worth surfacing even though the loop itself is too
        // short for PR-5.7 to draw.
        foreach (int stranded in everyone
            .Where(s => !claimed.Contains(s) && !inACycle.Contains(s) && waitsOn.ContainsKey(s))
            .Order())
        {
            List<int> path = WalkToCycle(stranded, waitsOn);

            if (path.Count > 2)
            {
                chains.Add(path);
            }
        }

        return chains.OrderByDescending(c => c.Count).ToArray();
    }

    /// <summary>
    /// Every path from a holder outwards to the sessions ultimately waiting on it.
    /// </summary>
    /// <remarks>
    /// Iterative rather than recursive, with the visited set carried per path. Recursion
    /// would be shorter but its depth is bounded by the data, and the data can contain a
    /// cycle.
    /// </remarks>
    private static List<IReadOnlyList<int>> WalkFrom(int root, Dictionary<int, List<int>> waitedOnBy)
    {
        List<IReadOnlyList<int>> complete = [];
        Stack<List<int>> pending = new();
        pending.Push([root]);

        while (pending.Count > 0)
        {
            List<int> path = pending.Pop();
            int tip = path[^1];

            List<int> next = waitedOnBy.TryGetValue(tip, out List<int>? waiters)
                ? waiters.Where(w => !path.Contains(w)).Order().ToList()
                : [];

            if (next.Count == 0)
            {
                complete.Add(path);
                continue;
            }

            foreach (int waiter in next)
            {
                pending.Push([.. path, waiter]);
            }
        }

        return complete;
    }

    /// <summary>
    /// The loop this session sits in, or empty when it is not in one.
    /// </summary>
    /// <remarks>
    /// Returned starting from its lowest session id, so the same deadlock renders the same
    /// way on every refresh. Without a fixed starting point a user watching a stuck instance
    /// would see the chain rotate and read it as the situation changing.
    /// </remarks>
    private static List<int> WalkCycle(int start, Dictionary<int, List<int>> waitsOn)
    {
        List<int> path = WalkToCycle(start, waitsOn);

        if (path.Count == 0)
        {
            return [];
        }

        // The walk ends where it repeats itself, so the loop is the tail from that repeat.
        int closes = path[^1];
        int entry = path.IndexOf(closes);

        if (entry == path.Count - 1)
        {
            return [];
        }

        List<int> loop = [.. path[entry..^1]];

        // Rotate to the lowest id so the same loop always reads the same way.
        int lowest = loop.IndexOf(loop.Min());
        return [.. loop[lowest..], .. loop[..lowest]];
    }

    /// <summary>
    /// Follows the waits from one session until they end or repeat.
    /// </summary>
    /// <remarks>
    /// The returned path ends with a repeated session id when it closed into a loop, and
    /// with a session waiting on nothing when it did not. Iterative with a visited set
    /// rather than recursive: the depth is bounded by the data, and the data can be a cycle.
    /// </remarks>
    private static List<int> WalkToCycle(int start, Dictionary<int, List<int>> waitsOn)
    {
        List<int> path = [];
        HashSet<int> seen = [];
        int current = start;

        while (true)
        {
            path.Add(current);

            if (!seen.Add(current))
            {
                return path;
            }

            if (!waitsOn.TryGetValue(current, out List<int>? holders) || holders.Count == 0)
            {
                return path.Count > 1 ? path : [];
            }

            current = holders.Min();
        }
    }
}
