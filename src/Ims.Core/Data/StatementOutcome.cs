namespace Ims.Core.Data;

/// <summary>
/// The outcome of one statement within an executed script.
/// </summary>
/// <remarks>
/// PR-3.4 requires a multi-statement script to present "each result or error in
/// sequence and indicating clearly which statement failed". That is why this type
/// carries the statement's index and its position in the script text, not only its
/// result — the editor needs to be able to point at the offending statement.
/// </remarks>
public sealed record StatementOutcome
{
    /// <summary>Zero-based position of the statement within the script.</summary>
    public required int Index { get; init; }

    /// <summary>The statement text as submitted (PR-8.2).</summary>
    public required string Sql { get; init; }

    /// <summary>Character offset of the statement within the original script text.</summary>
    public required int ScriptOffset { get; init; }

    public required StatementResultKind Kind { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="StatementResultKind.RowSet"/>.</summary>
    public IStatementResult? Result { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="StatementResultKind.RowsAffected"/>.</summary>
    public long? RowsAffected { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="StatementResultKind.Failed"/>.</summary>
    public InformixError? Error { get; init; }

    /// <summary>Server time for this statement alone (PR-4.3, PR-3.12).</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Transaction state after this statement ran (PR-3.7).</summary>
    public TransactionState TransactionState { get; init; }

    public bool Succeeded => Kind is not (StatementResultKind.Failed or StatementResultKind.Skipped);
}

/// <summary>
/// Whether work is pending on the connection.
/// </summary>
/// <remarks>
/// PR-3.7 requires transaction state to be visible "at all times", and explicit
/// commit or rollback when autocommit is off. A user who does not know they are
/// inside an open transaction is the mechanism behind more than one production
/// incident, so this is modelled rather than inferred.
/// </remarks>
public enum TransactionState
{
    /// <summary>The database is unlogged, so transactions do not apply.</summary>
    NotApplicable = 0,

    /// <summary>Autocommit: each statement commits on its own.</summary>
    AutoCommit = 1,

    /// <summary>A transaction is open with no uncommitted work yet.</summary>
    Open = 2,

    /// <summary>A transaction is open and has uncommitted work. Requires PR-3.7 prompting.</summary>
    Uncommitted = 3,

    /// <summary>The transaction failed and can only be rolled back.</summary>
    Failed = 4,
}
