using Ims.Core.Connections;

namespace Ims.Core.Data;

/// <summary>Lifecycle of a connection, as the UI needs to see it (PR-1.7).</summary>
public enum SessionState
{
    Closed = 0,
    Connecting = 1,
    Open = 2,

    /// <summary>Executing. The UI shows this and offers cancel (PR-3.5).</summary>
    Executing = 3,

    /// <summary>The connection dropped. PR-1.7 requires saying so clearly and offering reconnect.</summary>
    Broken = 4,
}

public sealed class SessionStateChangedEventArgs(SessionState previous, SessionState current, InformixError? error)
    : EventArgs
{
    public SessionState Previous { get; } = previous;

    public SessionState Current { get; } = current;

    /// <summary>Set when the transition was caused by a failure.</summary>
    public InformixError? Error { get; } = error;
}

/// <summary>
/// One connection to one Informix instance.
/// </summary>
/// <remarks>
/// <para>
/// Every server-touching method is asynchronous and takes a
/// <see cref="CancellationToken"/>. That is not a stylistic choice: NFR-1 requires
/// the UI never to block on server or network work, and PR-8.5 makes perceived
/// slowness a defect. An interface that offered a synchronous overload would make
/// violating both of those a one-character mistake.
/// </para>
/// <para>
/// The interface also deliberately offers no "execute arbitrary internal query"
/// affordance beyond what a caller passes in, because PR-6.2 requires that IMS
/// send no statement the user did not type or explicitly request.
/// </para>
/// </remarks>
public interface IInformixSession : IAsyncDisposable
{
    /// <summary>Which instance this session targets. Shown wherever the session is (PR-1.6).</summary>
    ConnectionDescriptor Descriptor { get; }

    SessionState State { get; }

    /// <summary>Visible at all times in the UI (PR-3.7).</summary>
    TransactionState TransactionState { get; }

    /// <summary>Null until connected; then the instance's reported capabilities (NFR-4).</summary>
    InformixServerInfo? ServerInfo { get; }

    /// <summary>Raised on every lifecycle transition, including an unexpected drop (PR-1.7).</summary>
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>Opens the connection, resolving the credential at the point of use (DEC-9).</summary>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs a script, yielding each statement's outcome as it completes (PR-3.4).
    /// </summary>
    /// <remarks>
    /// Streaming the outcomes rather than returning a list is what lets the editor
    /// show statement 1's rows while statement 2 is still running — the difference
    /// between a tool that feels fast and one that does not (PR-8.5).
    /// </remarks>
    IAsyncEnumerable<StatementOutcome> ExecuteScriptAsync(
        string script,
        CancellationToken cancellationToken);

    /// <summary>Runs one statement and returns its streaming result.</summary>
    Task<StatementOutcome> ExecuteAsync(string sql, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the statement currently running, without dropping the session or the
    /// application (PR-3.5).
    /// </summary>
    Task CancelAsync(CancellationToken cancellationToken);

    /// <summary>Commits the open transaction (PR-3.7).</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Rolls back the open transaction (PR-3.7).</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Opens sessions. The seam a different provider would be substituted at.</summary>
public interface IInformixSessionFactory
{
    /// <summary>
    /// Creates a session. Does not connect — call <see cref="IInformixSession.OpenAsync"/>.
    /// </summary>
    IInformixSession Create(ConnectionDescriptor descriptor, ICredentialResolver credentials);
}

/// <summary>
/// Supplies the secret for a connection at the moment it is needed.
/// </summary>
/// <remarks>
/// An interface rather than a password field so that DEC-9 holds structurally: the
/// secret is fetched from Windows Credential Manager at connect time and is never
/// stored on a descriptor, never serialised, and never available to be logged
/// (PR-6.3).
/// </remarks>
public interface ICredentialResolver
{
    /// <summary>
    /// Returns the password for the given connection, or null when none is stored.
    /// </summary>
    Task<string?> GetPasswordAsync(ConnectionDescriptor descriptor, CancellationToken cancellationToken);
}
