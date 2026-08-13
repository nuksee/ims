namespace Ims.Core.Monitoring;

/// <summary>
/// Reads live session state from <c>sysmaster</c> (PR-5.1 to PR-5.6).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Catalog.ICatalogReader"/> because a session is not schema —
/// but implemented by the same object, because it is the same connection. An Informix
/// connection has one cursor, so a session query and a catalogue query must share one
/// gate; a second decorator with a second semaphore would be two gates over one cursor,
/// which is the exact bug <see cref="Catalog.SerializedCatalogReader"/> exists to prevent.
/// </para>
/// <para>
/// A refresh therefore queues behind a tree expansion. That is the accepted cost of not
/// spending a third session per instance on the monitor, which PR-6.4 asks IMS not to do.
/// If it proves annoying in daily use, the fix is a decision about PR-6.4 rather than a
/// change to make first.
/// </para>
/// <para>
/// Every method is asynchronous and cancellable, like the catalogue's. But note what
/// cancellation can and cannot do here: <c>OdbcCommand.Cancel</c> does not reach this
/// server (measured 2026-08-06), so a token stops IMS waiting and the statement runs on.
/// That is why every query behind this interface is bounded before it is sent — a row cap
/// and a short timeout — rather than relying on being stopped once running (RSK-5).
/// </para>
/// </remarks>
public interface ISessionMonitor
{
    /// <summary>
    /// The session list (PR-5.1) and the lock-wait edges behind it (PR-5.3).
    /// </summary>
    /// <remarks>
    /// Returns <see cref="SessionSnapshot.Unavailable"/> rather than throwing when
    /// <c>sysmaster</c> cannot be read. Under PR-6.1 an ordinary account may legitimately
    /// lack that access, so it is a fact to report, not a fault to raise.
    /// </remarks>
    Task<SessionSnapshot> GetSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Detail for one session (PR-5.2): locks, resources, current SQL.</summary>
    Task<SessionDetail> GetSessionDetailAsync(int sid, CancellationToken cancellationToken);

    /// <summary>Instance indicators (PR-5.6). None of them needs privileged access.</summary>
    Task<InstanceIndicators> GetInstanceIndicatorsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether <c>sysmaster</c> answered at all (PR-6.1, NFR-4).
    /// </summary>
    /// <remarks>
    /// Null until the first read, because IMS does not probe for a capability nobody has
    /// asked about — opening the monitor is what licenses the question (PR-6.2). Remembered
    /// afterwards so a refused instance is asked once rather than on every refresh.
    /// </remarks>
    bool? SysMasterReadable { get; }
}
