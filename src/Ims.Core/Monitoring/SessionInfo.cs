namespace Ims.Core.Monitoring;

/// <summary>
/// One session on the instance, as PR-5.1 asks for it.
/// </summary>
/// <remarks>
/// Everything but the session id and the user name is nullable, and that is not
/// defensiveness. <c>sysmaster</c>'s shape varies, and a column IMS could not read has to
/// be distinguishable from one that came back empty — otherwise the UI cannot tell the
/// user which it is looking at (NFR-4).
/// </remarks>
public sealed record SessionInfo
{
    /// <summary>The session id. What <c>onstat -g ses</c> calls the sid.</summary>
    public required int Sid { get; init; }

    public required string UserName { get; init; }

    /// <summary>
    /// Where the session connected from (PR-5.1).
    /// </summary>
    /// <remarks>
    /// Through a connection pool or a JDBC gateway this is the gateway's host, not the
    /// user's desk. IMS reports what the server said and does not claim more (PR-8.4).
    /// </remarks>
    public string? HostName { get; init; }

    /// <summary>The client program, where the server records one (PR-5.1).</summary>
    public string? Application { get; init; }

    /// <summary>The client process id. A different number from <see cref="Sid"/>.</summary>
    public string? ProcessId { get; init; }

    /// <summary>
    /// When the session connected (PR-5.1).
    /// </summary>
    /// <remarks>
    /// Derived client-side from an epoch integer rather than read as a server-side
    /// duration. An INTERVAL column would be unreadable through this driver and would
    /// take every column after it down with it — see <c>SessionQueries</c>.
    /// </remarks>
    public DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>
    /// The state in words.
    /// </summary>
    /// <remarks>
    /// Never guesses: an unmapped code reads "Unknown (7)". A confident wrong "Running"
    /// on a session that is actually blocked would defeat the whole point of the view.
    /// </remarks>
    public required string State { get; init; }

    /// <summary>The server's own code, always preserved and shown on demand (PR-8.2).</summary>
    public required string RawState { get; init; }

    /// <summary>
    /// True when this is a session belonging to the user IMS is connected as (PR-5.4).
    /// </summary>
    /// <remarks>
    /// Matched on user name, so it marks every session that user has, not only the one
    /// IMS itself opened. That is what U1 wants: "is <em>my</em> work blocked".
    /// </remarks>
    public bool IsMine { get; init; }

    /// <summary>True for the server's own daemon sessions, which a filter can hide.</summary>
    public bool IsSystem { get; init; }
}
