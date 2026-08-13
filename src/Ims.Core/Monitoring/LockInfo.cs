namespace Ims.Core.Monitoring;

/// <summary>One lock a session holds (PR-5.2).</summary>
public sealed record LockInfo
{
    /// <summary>The session holding it.</summary>
    public required int OwnerSid { get; init; }

    public string? DatabaseName { get; init; }

    public string? TableName { get; init; }

    /// <summary>The lock mode in words, or "Unknown (…)" for a code IMS does not map.</summary>
    public required string LockType { get; init; }

    /// <summary>The server's own type code, preserved for PR-8.2.</summary>
    public required string RawLockType { get; init; }

    /// <summary>The row locked, where the lock is a row lock rather than a table one.</summary>
    public string? RowId { get; init; }

    /// <summary>The index, where the lock is on a key.</summary>
    public int? KeyNumber { get; init; }

    /// <summary>What is locked, qualified — <c>stores:orders</c>. Null when unnameable.</summary>
    public string? Resource =>
        DatabaseName is { Length: > 0 } db && TableName is { Length: > 0 } tab
            ? $"{db}:{tab}"
            : TableName;
}

/// <summary>
/// What a session is consuming (PR-5.2).
/// </summary>
/// <remarks>
/// <para>
/// Every field is nullable and the whole record may be absent, because the
/// <c>sysrstcb</c> columns these come from are the least certain in this slice. An
/// unavailable counter reads as unavailable; it does not read as zero. A zero would be a
/// claim, and PR-8.4 rules out presenting an inference as a fact.
/// </para>
/// <para>
/// Per-session temporary space is deliberately absent. Deriving it needs a partition
/// shape IMS does not read, and PR-5.2 is better served by an honest gap than by a
/// number nobody can justify. Temporary space appears at the instance level instead.
/// </para>
/// </remarks>
public sealed record SessionResources
{
    public long? MemoryTotalBytes { get; init; }

    public long? MemoryUsedBytes { get; init; }
}
