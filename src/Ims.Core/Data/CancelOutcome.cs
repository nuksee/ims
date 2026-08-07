namespace Ims.Core.Data;

/// <summary>
/// What a cancel request achieved.
/// </summary>
/// <remarks>
/// <para>
/// PR-3.5 asks that a running statement be stopped without losing the session.
/// Measured against 14.10 on 2026-08-06, the first half does not hold:
/// <c>OdbcCommand.Cancel()</c> does not reach the server, on a sorting or a
/// scanning workload alike, and the statement runs to completion or to its
/// timeout. The session survives, so the second half does.
/// </para>
/// <para>
/// That leaves IMS able to stop <em>waiting</em> but not able to stop the
/// <em>statement</em>, and those are different things. Modelling them separately is
/// the point of this type: PR-8.4 rules out presenting an inference as a fact, and
/// "Cancelled" over a statement still burning server CPU is exactly that.
/// </para>
/// </remarks>
public enum CancelOutcome
{
    /// <summary>Nothing was running, so there was nothing to stop.</summary>
    NothingRunning = 0,

    /// <summary>
    /// The server accepted the cancel and the statement stopped.
    /// </summary>
    /// <remarks>
    /// Not currently reachable over the ODBC provider — kept because it is what the
    /// requirement asks for, and because a driver or server upgrade could make it
    /// true. The smoke test's <c>--recheck-cancellation</c> is how that gets noticed.
    /// </remarks>
    StatementStopped = 1,

    /// <summary>
    /// IMS stopped waiting, but the statement is still running on the server.
    /// </summary>
    /// <remarks>
    /// The honest description of today's behaviour. The editor is usable again and
    /// the session is intact, but the work continues server-side until it finishes or
    /// is stopped out of band — which the user has to be told, or they will assume
    /// their runaway query is gone when it is not.
    /// </remarks>
    StoppedWaitingOnly = 2,
}
