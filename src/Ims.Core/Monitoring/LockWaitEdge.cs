namespace Ims.Core.Monitoring;

/// <summary>
/// One session waiting on a resource another session holds (PR-5.3).
/// </summary>
/// <remarks>
/// An edge, deliberately — "A waits on B" composes into the chains PR-5.7 asks for
/// without the pair having to know about them.
/// </remarks>
public sealed record LockWaitEdge
{
    /// <summary>The session that is waiting.</summary>
    public required int WaiterSid { get; init; }

    /// <summary>The session holding what it is waiting for.</summary>
    public required int HolderSid { get; init; }

    /// <summary>
    /// What is being contended, qualified as the server names it — <c>stores:orders</c>.
    /// </summary>
    /// <remarks>
    /// Null where the lock is on something IMS cannot name. The chain still resolves; it
    /// just cannot say what over.
    /// </remarks>
    public string? Resource { get; init; }

    /// <summary>The lock mode the waiter asked for, as the server spells it.</summary>
    public string? WaiterLockType { get; init; }

    /// <summary>The lock mode the holder has.</summary>
    public string? HolderLockType { get; init; }
}

/// <summary>
/// How much IMS could establish about who blocks whom (PR-5.3, NFR-4).
/// </summary>
/// <remarks>
/// <para>
/// Three tiers rather than a bool, for the same reason
/// <see cref="Catalog.StatisticsCurrency"/> has an Unknown: a named blocker that is wrong
/// is worse than an admitted absence, because someone might act on it — and acting means
/// interrupting a colleague's work.
/// </para>
/// <para>
/// The distinction between <see cref="ContentionOnly"/> and
/// <see cref="BlockerIdentified"/> is real and not pedantry. Finding two sessions on one
/// resource is not the same as finding one blocked by the other: two shared locks coexist
/// happily. Only incompatible modes make it a block, and the wording the UI uses differs
/// accordingly.
/// </para>
/// </remarks>
public enum LockWaitFidelity
{
    /// <summary>IMS could not determine lock waits at all. A stated absence, not an error.</summary>
    Unknown,

    /// <summary>Sessions contend on a resource; which one waits is not established.</summary>
    ContentionOnly,

    /// <summary>Waiter and holder are both identified. What PR-5.3 asks for.</summary>
    BlockerIdentified,
}
