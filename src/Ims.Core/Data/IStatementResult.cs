namespace Ims.Core.Data;

/// <summary>
/// The rows produced by one statement, read as a stream.
/// </summary>
/// <remarks>
/// <para>
/// The single most important shape in the provider abstraction. PR-4.2 requires
/// result sets to stream and page "rather than materialising them, so an
/// unbounded <c>SELECT</c> degrades gracefully instead of exhausting memory",
/// and RSK-6 names the hung client as a live risk.
/// </para>
/// <para>
/// Returning <see cref="IAsyncEnumerable{T}"/> rather than a <c>DataTable</c>
/// makes that structural: there is no API here that can load a million rows into
/// memory, so no call site can accidentally do it later.
/// </para>
/// </remarks>
public interface IStatementResult : IAsyncDisposable
{
    /// <summary>Column metadata, available before the first row is read.</summary>
    IReadOnlyList<ResultColumn> Columns { get; }

    /// <summary>
    /// Rows consumed so far. Grows as the caller reads; it is not the total, which
    /// is unknown until the stream ends (PR-4.3).
    /// </summary>
    long RowsRead { get; }

    /// <summary>True once the server has no more rows to give.</summary>
    bool IsComplete { get; }

    /// <summary>
    /// True when rows were dropped to bound memory, so the UI can say so.
    /// </summary>
    /// <remarks>
    /// A single ODBC connection holds one cursor at a time, so every row-returning
    /// statement but the last in a script must be read into memory before the next
    /// can run. That read is capped, and a capped read has to admit it — a result
    /// silently missing rows is worse than one that says it is incomplete.
    /// </remarks>
    bool WasTruncated => false;

    /// <summary>
    /// Streams rows. Each row is a fresh array sized to <see cref="Columns"/>.
    /// </summary>
    /// <remarks>
    /// The token cancels mid-stream, which is half of PR-3.5 — a user who runs an
    /// unbounded <c>SELECT</c> must be able to stop reading it, not just stop
    /// starting it.
    /// </remarks>
    IAsyncEnumerable<InformixValue[]> ReadRowsAsync(CancellationToken cancellationToken);
}

/// <summary>What a statement produced.</summary>
public enum StatementResultKind
{
    /// <summary>A result set. <see cref="StatementOutcome.Result"/> is set.</summary>
    RowSet,

    /// <summary>A row count, from DML. <see cref="StatementOutcome.RowsAffected"/> is set.</summary>
    RowsAffected,

    /// <summary>Succeeded with neither rows nor a count — DDL, SET, and similar.</summary>
    NoResult,

    /// <summary>Failed. <see cref="StatementOutcome.Error"/> is set (PR-3.4).</summary>
    Failed,

    /// <summary>Not run, because an earlier statement failed and the user stopped the script.</summary>
    Skipped,
}
