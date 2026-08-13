namespace Ims.Core.Monitoring;

/// <summary>
/// Every session on the instance at one moment, and what is blocking what
/// (PR-5.1, PR-5.3).
/// </summary>
/// <remarks>
/// A snapshot, named as one. PR-5.5 makes refresh manual by default, so what is on screen
/// may be minutes old — <see cref="ReadAt"/> is required rather than optional so the UI
/// cannot forget to say when this was true.
/// </remarks>
public sealed record SessionSnapshot
{
    public required IReadOnlyList<SessionInfo> Sessions { get; init; }

    /// <summary>Waiter-to-holder edges (PR-5.3). Empty when nothing is blocked.</summary>
    public required IReadOnlyList<LockWaitEdge> Waits { get; init; }

    /// <summary>How much IMS could learn about blocking (PR-5.3, NFR-4).</summary>
    public required LockWaitFidelity Fidelity { get; init; }

    /// <summary>
    /// Sessions on the instance, counted separately from the list.
    /// </summary>
    /// <remarks>
    /// Counted by its own query rather than taken from <see cref="Sessions"/>, because the
    /// list is capped. Reporting the capped length as the session count would understate a
    /// busy instance at exactly the moment the number mattered.
    /// </remarks>
    public int? TotalSessionCount { get; init; }

    /// <summary>True when the row cap was reached and the list is therefore partial.</summary>
    public bool IsCapped { get; init; }

    /// <summary>When IMS read this.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>Every query behind this snapshot, including any that failed (PR-8.2).</summary>
    public required IReadOnlyList<ServerQuery> Queries { get; init; }

    /// <summary>
    /// Why there is nothing to show, when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Null on a successful read. Set when <c>sysmaster</c> could not be read at all —
    /// which under PR-6.1 is a legitimate state for an ordinary account, not a fault. IMS
    /// grants no capability the user does not already hold, so this is reported as a fact
    /// about their privileges rather than as an error.
    /// </remarks>
    public string? UnavailableReason { get; init; }

    /// <summary>True when IMS has something to show.</summary>
    public bool IsAvailable => UnavailableReason is null;

    /// <summary>A snapshot standing for "IMS could not read this", with the reason.</summary>
    public static SessionSnapshot Unavailable(string why, DateTimeOffset readAt) => new()
    {
        Sessions = [],
        Waits = [],
        Fidelity = LockWaitFidelity.Unknown,
        ReadAt = readAt,
        Queries = [],
        UnavailableReason = why,
    };
}

/// <summary>
/// One session's detail, in the shape PR-5.2 asks for.
/// </summary>
/// <remarks>
/// Built like <see cref="Catalog.TableDetail"/>: each section is read independently and a
/// section that fails costs itself, not the pane. Its query still appears in
/// <see cref="Queries"/> with a failed outcome, so the user can see what was asked and
/// why it did not answer.
/// </remarks>
public sealed record SessionDetail
{
    public required int Sid { get; init; }

    /// <summary>The list entry this detail belongs to, where it is still there.</summary>
    public SessionInfo? Session { get; init; }

    /// <summary>What the session is running, or last ran (PR-5.1).</summary>
    public string? CurrentSql { get; init; }

    /// <summary>
    /// True when <see cref="CurrentSql"/> was cut short.
    /// </summary>
    /// <remarks>
    /// Stated rather than silent. PR-8.2 says never hide the server, and quietly eliding
    /// the tail of a statement would be a small way of doing exactly that.
    /// </remarks>
    public bool CurrentSqlTruncated { get; init; }

    public required IReadOnlyList<LockInfo> LocksHeld { get; init; }

    /// <summary>Edges involving this session, in either direction.</summary>
    public required IReadOnlyList<LockWaitEdge> Waits { get; init; }

    /// <summary>Null when the server did not answer for these counters.</summary>
    public SessionResources? Resources { get; init; }

    public required IReadOnlyList<ServerQuery> Queries { get; init; }

    /// <summary>Sessions this one is waiting on.</summary>
    public IEnumerable<LockWaitEdge> Blockers => Waits.Where(w => w.WaiterSid == Sid);

    /// <summary>Sessions waiting on this one.</summary>
    public IEnumerable<LockWaitEdge> Blocking => Waits.Where(w => w.HolderSid == Sid);
}

/// <summary>
/// What the instance itself is doing (PR-5.6).
/// </summary>
/// <remarks>
/// <para>
/// Every field is nullable because PR-5.6 is a Should and each indicator comes from its
/// own query. One absent object costs one figure; the strip shows the rest.
/// </para>
/// <para>
/// Every one of these is readable without privileged access, which is the requirement's
/// actual constraint — an indicator needing DBA rights would be useless to U1 and would
/// breach PR-6.1's promise that IMS adds no capability.
/// </para>
/// </remarks>
public sealed record InstanceIndicators
{
    /// <summary>The raw version banner, shown verbatim (PR-8.2).</summary>
    public string? VersionBanner { get; init; }

    /// <summary>Online, quiescent and so on, in words.</summary>
    public string? Mode { get; init; }

    public string? RawMode { get; init; }

    /// <summary>
    /// How long the instance has been up.
    /// </summary>
    /// <remarks>
    /// Computed client-side from a boot timestamp. Asking the server for a duration would
    /// mean an INTERVAL, which this driver cannot read.
    /// </remarks>
    public TimeSpan? Uptime { get; init; }

    public int? SessionCount { get; init; }

    /// <summary>Read cache hit rate as a percentage, or null when it cannot be computed.</summary>
    public double? ReadCachePercent { get; init; }

    public double? WriteCachePercent { get; init; }

    public DateTimeOffset? LastCheckpoint { get; init; }

    public required IReadOnlyList<ServerQuery> Queries { get; init; }

    /// <summary>An empty set of indicators, for when none could be read.</summary>
    public static InstanceIndicators None => new() { Queries = [] };
}
