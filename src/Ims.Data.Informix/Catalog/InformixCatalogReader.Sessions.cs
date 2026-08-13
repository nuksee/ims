using System.Data.Odbc;
using System.Globalization;
using Ims.Core.Data;
using Ims.Core.Diagnostics;
using Ims.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Ims.Data.Informix.Catalog;

/// <summary>
/// The session monitor half of the reader (PR-5.1 to PR-5.6).
/// </summary>
/// <remarks>
/// <para>
/// A partial of <see cref="InformixCatalogReader"/> rather than a class of its own, and the
/// reason is the one cursor. An Informix connection has exactly one, so a session query and
/// a catalogue query cannot overlap — the second closes the first's result out from under
/// it. Sharing the object means sharing the connection, which means one gate in front of
/// both (<c>SerializedCatalogReader</c>) instead of two gates over one cursor, which is the
/// bug that gate exists to prevent.
/// </para>
/// <para>
/// It shares the object but not the file: the catalogue half is already long, and this is
/// easier to review on its own. Same type, same connection, same <c>QueryAsync</c>.
/// </para>
/// <para>
/// Every read here follows <c>GetTableDetailAsync</c>'s shape — a sequence of independently
/// failure-tolerant sub-reads, each appending what it ran to a shared list. In this slice
/// that is not just good manners: several of these <c>sysmaster</c> objects could not be
/// verified against a live server before the code was written, so a missing column has to
/// cost one section of one pane. NFR-4 asks for exactly that, and here it is load-bearing
/// rather than defensive.
/// </para>
/// </remarks>
public sealed partial class InformixCatalogReader
{
    /// <summary>Null until probed. See <see cref="SysMasterReadable"/>.</summary>
    private bool? _sysMasterReadable;

    /// <summary>Null until probed; whether lock detail is visible (PR-5.2, PR-5.3).</summary>
    private bool? _hasLockDetail;

    /// <summary>Null until probed; whether the full session list shape exists.</summary>
    private bool? _hasFullSessionColumns;

    /// <inheritdoc />
    public bool? SysMasterReadable => _sysMasterReadable;

    /// <summary>
    /// Whether lock detail answered, once asked (PR-5.2, PR-5.3).
    /// </summary>
    /// <remarks>
    /// Null until the first lock read. Exposed so the capability set can be filled in from
    /// what actually happened rather than from a version number (NFR-4).
    /// </remarks>
    public bool? HasLockDetail => _hasLockDetail;

    /// <summary>
    /// The capabilities this reader has actually observed (NFR-4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from what the server did, not from its version number. NFR-4 says nothing may
    /// branch on a version, so that an untested 12.10 degrades rather than fails — and the
    /// two Slice 3 members of <see cref="InformixCapability"/> were declared for exactly
    /// this and left unpopulated until there was something honest to put in them.
    /// </para>
    /// <para>
    /// Empty until the monitor has read something. That is deliberate rather than lazy:
    /// <c>ReadServerInfoAsync</c> leaves the set empty at connect for the same reason, since
    /// under PR-6.2 a capability nobody has asked about is one IMS has no business probing
    /// for. Opening the monitor is the documented action that licenses the question.
    /// </para>
    /// </remarks>
    public IReadOnlySet<InformixCapability> ObservedCapabilities
    {
        get
        {
            HashSet<InformixCapability> observed = [];

            if (_sysMasterReadable is true)
            {
                observed.Add(InformixCapability.SysMasterReadable);
            }

            if (_hasLockDetail is true)
            {
                observed.Add(InformixCapability.SessionLockDetail);
            }

            return observed;
        }
    }

    // ---- The session list -------------------------------------------------------

    /// <inheritdoc />
    public async Task<SessionSnapshot> GetSessionsAsync(CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Read the session list");

        DateTimeOffset readAt = DateTimeOffset.Now;
        List<ServerQuery> queries = [];

        // sysmaster is the gate for everything else here, so a refusal ends the read
        // rather than producing five more failures that all say the same thing.
        (IReadOnlyList<SessionInfo>? sessions, string? failure) = await ReadSessionsAsync(
            queries, cancellationToken).ConfigureAwait(false);

        if (sessions is null)
        {
            _sysMasterReadable = false;
            return SessionSnapshot.Unavailable(failure ?? "sysmaster could not be read.", readAt)
                with { Queries = queries };
        }

        _sysMasterReadable = true;

        int? total = await ReadSessionCountAsync(queries, cancellationToken).ConfigureAwait(false);

        (IReadOnlyList<LockWaitEdge> waits, LockWaitFidelity fidelity) = await ReadLockWaitsAsync(
            queries, cancellationToken).ConfigureAwait(false);

        return new SessionSnapshot
        {
            Sessions = sessions,
            Waits = waits,
            Fidelity = fidelity,
            TotalSessionCount = total,
            IsCapped = sessions.Count >= SessionQueries.RowCap,
            ReadAt = readAt,
            Queries = queries,
        };
    }

    /// <summary>
    /// The session list, falling back to a narrower shape if the full one is refused.
    /// </summary>
    /// <remarks>
    /// Returns null sessions only when even the narrow read failed, which means sysmaster
    /// itself is unreadable. The two-step is why PR-5.1's core survives a server that does
    /// not expose <c>feprogram</c> or <c>state</c>: those columns are worth having and are
    /// not worth the whole list.
    /// </remarks>
    private async Task<(IReadOnlyList<SessionInfo>? Sessions, string? Failure)> ReadSessionsAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -g ses";

        if (_hasFullSessionColumns is not false)
        {
            try
            {
                IReadOnlyList<SessionInfo> full = await QueryAsync(
                    SessionQueries.SessionList,
                    MapFullSession,
                    SessionQueries.TimeoutSeconds,
                    cancellationToken).ConfigureAwait(false);

                _hasFullSessionColumns = true;
                queries.Add(new ServerQuery("Sessions", SessionQueries.SessionList, onstat));
                return (full, null);
            }
            catch (OdbcException ex)
            {
                _hasFullSessionColumns = false;

                _logger.LogInformation(
                    "The full syssessions shape was refused, so the session list drops to "
                    + "id, user and host: {Message}",
                    ex.Message);

                queries.Add(new ServerQuery(
                    "Sessions", SessionQueries.SessionList, onstat,
                    ServerQueryOutcome.Failed, Describe(ex)));
            }
        }

        try
        {
            IReadOnlyList<SessionInfo> minimal = await QueryAsync(
                SessionQueries.SessionListMinimal,
                MapMinimalSession,
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            queries.Add(new ServerQuery(
                "Sessions (reduced)", SessionQueries.SessionListMinimal, onstat));

            return (minimal, null);
        }
        catch (OdbcException ex)
        {
            _logger.LogInformation("sysmaster:syssessions could not be read: {Message}", ex.Message);

            queries.Add(new ServerQuery(
                "Sessions (reduced)", SessionQueries.SessionListMinimal, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return (null, "This account cannot read sysmaster:syssessions. " + Describe(ex));
        }
    }

    private static SessionInfo MapFullSession(OdbcDataReader reader)
    {
        string? rawState = Trim(GetString(reader, 5));

        return new SessionInfo
        {
            Sid = GetInt(reader, 0) ?? 0,
            UserName = Trim(GetString(reader, 1)) ?? string.Empty,
            HostName = Trim(GetString(reader, 2)),
            ProcessId = Trim(GetString(reader, 3)),
            Application = Trim(GetString(reader, 4)),
            State = DescribeSessionState(rawState),
            RawState = rawState ?? string.Empty,
            ConnectedAt = FromUnixSeconds(GetLong(reader, 6)),
        };
    }

    private static SessionInfo MapMinimalSession(OdbcDataReader reader) => new()
    {
        Sid = GetInt(reader, 0) ?? 0,
        UserName = Trim(GetString(reader, 1)) ?? string.Empty,
        HostName = Trim(GetString(reader, 2)),
        State = "Unknown",
        RawState = string.Empty,
    };

    /// <summary>
    /// The true session count, which the capped list cannot give (PR-5.6).
    /// </summary>
    private async Task<int?> ReadSessionCountAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<int?> counted = await QueryAsync(
                SessionQueries.SessionCount,
                reader => GetInt(reader, 0),
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            queries.Add(new ServerQuery(
                "Session count", SessionQueries.SessionCount, "onstat -g ses"));

            return counted.Count > 0 ? counted[0] : null;
        }
        catch (OdbcException ex)
        {
            queries.Add(new ServerQuery(
                "Session count", SessionQueries.SessionCount, "onstat -g ses",
                ServerQueryOutcome.Failed, Describe(ex)));

            return null;
        }
    }

    // ---- Lock waits -------------------------------------------------------------

    /// <summary>
    /// Who is blocking whom, graded by how much could be established (PR-5.3).
    /// </summary>
    /// <remarks>
    /// The query finds sessions on one resource; the lock modes decide whether that is a
    /// block. When no pair has modes IMS can compare, the answer is contention rather than
    /// blocking, and the fidelity says so — a named blocker that is wrong is worse than an
    /// admitted absence, because someone might act on it.
    /// </remarks>
    private async Task<(IReadOnlyList<LockWaitEdge> Waits, LockWaitFidelity Fidelity)> ReadLockWaitsAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -g lok, onstat -K";

        try
        {
            IReadOnlyList<LockWaitEdge> edges = await QueryAsync(
                SessionQueries.LockWaits,
                MapLockWait,
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            _hasLockDetail = true;
            queries.Add(new ServerQuery("Lock waits", SessionQueries.LockWaits, onstat));

            // No lock-mode test here, deliberately. syslocks.waiter means the server has
            // already queued that session behind this lock — it is waiting by definition, and
            // Informix decided the modes conflict before it made anyone wait. Filtering on
            // AreIncompatibleLocks would discard every genuine wait, because one row records
            // only the holder's mode and a null waiter mode reads as "not established".
            LockWaitEdge[] blocking = edges
                .Where(e => e.WaiterSid != 0 && e.HolderSid != 0 && e.WaiterSid != e.HolderSid)
                .ToArray();

            // Nothing waiting at all is the ordinary state of a healthy instance, and a full
            // answer — so the fidelity is not downgraded. There is nothing IMS failed to
            // establish here.
            return (blocking, LockWaitFidelity.BlockerIdentified);
        }
        catch (OdbcException ex)
        {
            queries.Add(new ServerQuery(
                "Lock waits", SessionQueries.LockWaits, onstat,
                IsTimeout(ex) ? ServerQueryOutcome.TimedOut : ServerQueryOutcome.Failed,
                Describe(ex)));

            // A timeout means syslocks itself is too expensive to read on this instance, not
            // that this particular query was wrong — so the fallback, which reads the same
            // pseudo-table and joins it to itself, cannot possibly do better. Trying it anyway
            // doubles the wait to reach the same answer: measured 2026-08-13, the pair cost
            // over twenty seconds between them before reporting Unknown.
            //
            // Any other error is a shape problem — a missing waiter column, most likely — and
            // there the fallback is exactly the right thing to try.
            if (IsTimeout(ex))
            {
                _hasLockDetail = false;

                _logger.LogInformation(
                    "sysmaster:syslocks timed out, so lock waits cannot be reported on this "
                    + "instance. The contention fallback reads the same pseudo-table and is "
                    + "not attempted: {Message}",
                    ex.Message);

                queries.Add(new ServerQuery(
                    "Lock contention (fallback)", SessionQueries.LockContention, onstat,
                    ServerQueryOutcome.NotAttempted,
                    "Not sent: syslocks timed out, and this reads the same pseudo-table."));

                return ([], LockWaitFidelity.Unknown);
            }

            _logger.LogInformation(
                "sysmaster:syslocks.waiter did not answer, so IMS falls back to looking for "
                + "sessions contending on the same resource: {Message}",
                ex.Message);

            return await ReadLockContentionAsync(queries, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the server ran out of time rather than objecting to the statement.
    /// </summary>
    /// <remarks>
    /// <c>HYT00</c> is ODBC's timeout state and <c>HY008</c> is the cancel the driver raises
    /// alongside it. The two arrive together from this driver, so either is enough. Matched on
    /// SQLSTATE rather than on message text, which is localised.
    /// </remarks>
    private static bool IsTimeout(OdbcException ex) =>
        ex.Errors.Cast<OdbcError>().Any(e => IsTimeoutState(e.SQLState));

    /// <summary>
    /// Whether a SQLSTATE means "ran out of time" rather than "no".
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="IsTimeout"/> because <c>OdbcException</c> cannot be constructed
    /// in a test — the decision is what matters, so the decision is what is testable.
    /// </remarks>
    internal static bool IsTimeoutState(string? sqlState) =>
        sqlState is "HYT00" or "HYT01" or "HY008";

    /// <summary>
    /// The fallback: sessions holding locks on one resource, which is contention, not blocking.
    /// </summary>
    /// <remarks>
    /// Measured to time out at ten seconds against 14.10 on 2026-08-13 — the join is quadratic
    /// over an unindexed pseudo-table — so this usually fails, and failing is handled. It earns
    /// its place only because a server without a <c>waiter</c> column would otherwise show no
    /// lock information at all.
    /// </remarks>
    private async Task<(IReadOnlyList<LockWaitEdge> Waits, LockWaitFidelity Fidelity)> ReadLockContentionAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -g lok, onstat -K";

        try
        {
            IReadOnlyList<LockWaitEdge> edges = await QueryAsync(
                SessionQueries.LockContention,
                MapLockContention,
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            _hasLockDetail = true;
            queries.Add(new ServerQuery(
                "Lock contention (fallback)", SessionQueries.LockContention, onstat));

            // Never BlockerIdentified from this query: two sessions on one resource may both
            // hold compatible locks and block nothing. Which one waits is not established, so
            // the UI must not say one is blocked.
            return edges.Count == 0
                ? ([], LockWaitFidelity.Unknown)
                : (edges, LockWaitFidelity.ContentionOnly);
        }
        catch (OdbcException ex)
        {
            _hasLockDetail = false;

            bool timedOut = IsTimeout(ex);

            // Two calls rather than one with a chosen template: a template that varies between
            // calls cannot be grouped or searched on, which is most of what structured logging
            // is for.
            if (timedOut)
            {
                _logger.LogInformation(
                    "sysmaster:syslocks timed out, so lock waits cannot be reported on this "
                    + "instance: {Message}",
                    ex.Message);
            }
            else
            {
                _logger.LogInformation(
                    "This server does not expose sysmaster:syslocks to IMS, so lock waits "
                    + "cannot be reported: {Message}",
                    ex.Message);
            }

            queries.Add(new ServerQuery(
                "Lock contention (fallback)", SessionQueries.LockContention, onstat,
                timedOut ? ServerQueryOutcome.TimedOut : ServerQueryOutcome.Failed,
                Describe(ex)));

            return ([], LockWaitFidelity.Unknown);
        }
    }

    private static LockWaitEdge MapLockWait(OdbcDataReader reader)
    {
        string? holderType = Trim(GetString(reader, 6));

        return new LockWaitEdge
        {
            WaiterSid = GetInt(reader, 0) ?? 0,
            HolderSid = GetInt(reader, 1) ?? 0,
            Resource = Qualify(Trim(GetString(reader, 2)), Trim(GetString(reader, 3))),

            // One row describes one lock: the mode belongs to the holder. What the waiter
            // asked for is not recorded here, so it is left null rather than guessed — and
            // AreIncompatibleLocks reads a null as "not established", which downgrades the
            // claim rather than inventing one.
            HolderLockType = holderType,
            WaiterLockType = null,
        };
    }

    private static LockWaitEdge MapLockContention(OdbcDataReader reader) => new()
    {
        WaiterSid = GetInt(reader, 0) ?? 0,
        HolderSid = GetInt(reader, 1) ?? 0,
        Resource = Qualify(Trim(GetString(reader, 2)), Trim(GetString(reader, 3))),
        WaiterLockType = Trim(GetString(reader, 6)),
        HolderLockType = Trim(GetString(reader, 7)),
    };

    // ---- One session's detail ---------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Three keyed reads, and deliberately <em>not</em> a fourth for the lock waits. This used
    /// to re-read every wait on the instance just to filter it to one session — work the
    /// snapshot had already done — so clicking through five sessions ran the most expensive
    /// query in the slice six times. Against 14.10 that was six ten-second timeouts, and
    /// because the monitor shares its connection with the object tree, each one held the
    /// semaphore and stalled tree expansion with it.
    /// </para>
    /// <para>
    /// The caller passes in what the snapshot already knows instead. Slightly stale is the
    /// right trade here: the edges are as old as the list the user is looking at, which is what
    /// PR-5.5 means by manual refresh, and re-reading them per click bought no freshness worth
    /// ten seconds of a shared connection (PR-6.4).
    /// </para>
    /// </remarks>
    public async Task<SessionDetail> GetSessionDetailAsync(
        int sid,
        IReadOnlyList<LockWaitEdge> knownWaits,
        CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Read session detail");

        List<ServerQuery> queries = [];

        (string? sql, bool truncated) = await ReadCurrentSqlAsync(sid, queries, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<LockInfo> locks = await ReadLocksHeldAsync(sid, queries, cancellationToken)
            .ConfigureAwait(false);

        SessionResources? resources = await ReadSessionResourcesAsync(sid, queries, cancellationToken)
            .ConfigureAwait(false);

        return new SessionDetail
        {
            Sid = sid,
            CurrentSql = sql,
            CurrentSqlTruncated = truncated,
            LocksHeld = locks,
            Waits = knownWaits.Where(w => w.WaiterSid == sid || w.HolderSid == sid).ToArray(),
            Resources = resources,
            Queries = queries,
        };
    }

    private async Task<(string? Sql, bool Truncated)> ReadCurrentSqlAsync(
        int sid,
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -g sql";

        try
        {
            IReadOnlyList<string?> statements = await QueryAsync(
                SessionQueries.CurrentSql,
                reader => GetString(reader, 1),
                SessionQueries.TimeoutSeconds,
                cancellationToken,
                sid).ConfigureAwait(false);

            queries.Add(new ServerQuery("Current SQL", SessionQueries.CurrentSql, onstat));

            string? text = statements.Count > 0 ? statements[0]?.TrimEnd() : null;

            // The server truncated at the cap, so anything of exactly that length may have
            // more behind it. Said rather than hidden — PR-8.2.
            return (text, text is not null && text.Length >= SessionQueries.CurrentSqlLength);
        }
        catch (OdbcException ex)
        {
            _logger.LogInformation(
                "This server does not expose sysmaster:syssqlcurses as IMS expects, so the "
                + "current statement cannot be shown: {Message}",
                ex.Message);

            queries.Add(new ServerQuery(
                "Current SQL", SessionQueries.CurrentSql, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return (null, false);
        }
    }

    private async Task<IReadOnlyList<LockInfo>> ReadLocksHeldAsync(
        int sid,
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -g lok";

        try
        {
            IReadOnlyList<LockInfo> locks = await QueryAsync(
                SessionQueries.LocksHeld,
                MapLock,
                SessionQueries.TimeoutSeconds,
                cancellationToken,
                sid).ConfigureAwait(false);

            queries.Add(new ServerQuery("Locks held", SessionQueries.LocksHeld, onstat));
            return locks;
        }
        catch (OdbcException ex)
        {
            queries.Add(new ServerQuery(
                "Locks held", SessionQueries.LocksHeld, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return [];
        }
    }

    private static LockInfo MapLock(OdbcDataReader reader)
    {
        string? rawType = Trim(GetString(reader, 5));

        return new LockInfo
        {
            OwnerSid = GetInt(reader, 0) ?? 0,
            DatabaseName = Trim(GetString(reader, 1)),
            TableName = Trim(GetString(reader, 2)),
            RowId = Trim(GetString(reader, 3)),
            KeyNumber = GetInt(reader, 4),
            LockType = DescribeLockType(rawType),
            RawLockType = rawType ?? string.Empty,
        };
    }

    /// <summary>
    /// A session's resource counters (PR-5.2).
    /// </summary>
    /// <remarks>
    /// The least certain read in the slice — every <c>sysrstcb</c> column name here is
    /// unverified against a live server. Returns null rather than zeroes when it fails, so
    /// the pane can say "not available on this server" instead of claiming a session is
    /// using no memory.
    /// </remarks>
    private async Task<SessionResources?> ReadSessionResourcesAsync(
        int sid,
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -g ses <sid>";

        try
        {
            IReadOnlyList<SessionResources> resources = await QueryAsync(
                SessionQueries.SessionResources,
                reader => new SessionResources
                {
                    MemoryTotalBytes = GetLong(reader, 1),
                    MemoryUsedBytes = GetLong(reader, 2),
                },
                SessionQueries.TimeoutSeconds,
                cancellationToken,
                sid).ConfigureAwait(false);

            queries.Add(new ServerQuery("Resources", SessionQueries.SessionResources, onstat));
            return resources.Count > 0 ? resources[0] : null;
        }
        catch (OdbcException ex)
        {
            _logger.LogInformation(
                "This server does not expose sysmaster:sysrstcb as IMS expects, so session "
                + "resource use cannot be reported: {Message}",
                ex.Message);

            queries.Add(new ServerQuery(
                "Resources", SessionQueries.SessionResources, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return null;
        }
    }

    // ---- Instance indicators ----------------------------------------------------

    /// <inheritdoc />
    public async Task<InstanceIndicators> GetInstanceIndicatorsAsync(CancellationToken cancellationToken)
    {
        ServerCallGuard.AssertNotOnUiThread("Read instance indicators");

        List<ServerQuery> queries = [];

        (string? mode, string? rawMode, DateTimeOffset? booted) =
            await ReadServerStateAsync(queries, cancellationToken).ConfigureAwait(false);

        (double? read, double? write) = await ReadCacheRatiosAsync(queries, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset? checkpoint = await ReadLastCheckpointAsync(queries, cancellationToken)
            .ConfigureAwait(false);

        int? sessions = await ReadSessionCountAsync(queries, cancellationToken).ConfigureAwait(false);

        return new InstanceIndicators
        {
            Mode = mode,
            RawMode = rawMode,
            Uptime = booted is { } boot ? DateTimeOffset.Now - boot : null,
            SessionCount = sessions,
            ReadCachePercent = read,
            WriteCachePercent = write,
            LastCheckpoint = checkpoint,
            Queries = queries,
        };
    }

    private async Task<(string? Mode, string? RawMode, DateTimeOffset? Booted)> ReadServerStateAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -";

        try
        {
            IReadOnlyList<(int? Mode, long? Boot)> state = await QueryAsync(
                SessionQueries.ServerState,
                reader => (GetInt(reader, 0), GetLong(reader, 1)),
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            queries.Add(new ServerQuery("Server state", SessionQueries.ServerState, onstat));

            if (state.Count == 0)
            {
                return (null, null, null);
            }

            (int? raw, long? boot) = state[0];
            return (
                DescribeServerMode(raw),
                raw?.ToString(CultureInfo.InvariantCulture),
                FromUnixSeconds(boot));
        }
        catch (OdbcException ex)
        {
            queries.Add(new ServerQuery(
                "Server state", SessionQueries.ServerState, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return (null, null, null);
        }
    }

    private async Task<(double? Read, double? Write)> ReadCacheRatiosAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -p";

        try
        {
            IReadOnlyList<(string? Name, long? Value)> rows = await QueryAsync(
                SessionQueries.Profile,
                reader => (Trim(GetString(reader, 0)), GetLong(reader, 1)),
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            queries.Add(new ServerQuery("Buffer efficiency", SessionQueries.Profile, onstat));

            long? Value(string name) => rows
                .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                .Value;

            return (
                ComputeCacheRatio(Value("bufreads"), Value("dskreads")),
                ComputeCacheRatio(Value("bufwrits"), Value("dskwrits")));
        }
        catch (OdbcException ex)
        {
            queries.Add(new ServerQuery(
                "Buffer efficiency", SessionQueries.Profile, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return (null, null);
        }
    }

    private async Task<DateTimeOffset?> ReadLastCheckpointAsync(
        List<ServerQuery> queries,
        CancellationToken cancellationToken)
    {
        const string onstat = "onstat -m";

        try
        {
            IReadOnlyList<long?> stamps = await QueryAsync(
                SessionQueries.LastCheckpoint,
                reader => GetLong(reader, 0),
                SessionQueries.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            queries.Add(new ServerQuery("Last checkpoint", SessionQueries.LastCheckpoint, onstat));

            return stamps.Count > 0 ? FromUnixSeconds(stamps[0]) : null;
        }
        catch (OdbcException ex)
        {
            queries.Add(new ServerQuery(
                "Last checkpoint", SessionQueries.LastCheckpoint, onstat,
                ServerQueryOutcome.Failed, Describe(ex)));

            return null;
        }
    }

    // ---- Translation, all pure and therefore all tested -------------------------

    /// <summary>
    /// A session state code in words (PR-5.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping is provisional: whether <c>syssessions.state</c> arrives as a bitmask or
    /// a string could not be confirmed against a live server, so this reads it as text and
    /// handles either. What is <em>not</em> provisional is the fallback — an unrecognised
    /// code reads "Unknown (7)" and never "Running".
    /// </para>
    /// <para>
    /// That matters more here than anywhere else in the slice. A confident wrong "Running"
    /// on a session that is actually blocked would defeat the entire view, and it would do
    /// it silently: the user would look at the one screen built to tell them they are
    /// blocked and be told everything is fine.
    /// </para>
    /// </remarks>
    internal static string DescribeSessionState(string? raw)
    {
        string? state = raw?.Trim();

        if (string.IsNullOrEmpty(state))
        {
            return "Unknown";
        }

        // A word from the server wins: it is more specific than anything IMS would infer,
        // and PR-8.2's habit is to prefer the server's own vocabulary.
        if (!int.TryParse(state, CultureInfo.InvariantCulture, out int bits))
        {
            return state;
        }

        if (bits == 0)
        {
            return "Running";
        }

        string[] waits = [];

        if ((bits & 1) != 0) { waits = [.. waits, "a mutex"]; }
        if ((bits & 2) != 0) { waits = [.. waits, "a condition"]; }
        if ((bits & 4) != 0) { waits = [.. waits, "a lock"]; }
        if ((bits & 8) != 0) { waits = [.. waits, "a log buffer"]; }
        if ((bits & 16) != 0) { waits = [.. waits, "a transaction"]; }

        return waits.Length == 0
            ? $"Unknown ({state})"
            : "Waiting on " + string.Join(", ", waits);
    }

    /// <summary>A lock mode in words, preserving the server's code on the way through.</summary>
    internal static string DescribeLockType(string? raw)
    {
        string? type = raw?.Trim().ToUpperInvariant();

        return type switch
        {
            null or "" => "Unknown",
            "S" => "Shared",
            "X" => "Exclusive",
            "U" => "Update",
            "B" or "IS" => "Intent shared",
            "IX" => "Intent exclusive",
            "SIX" => "Shared with intent exclusive",
            _ => $"Unknown ({raw?.Trim()})",
        };
    }

    /// <summary>
    /// The instance's mode in words (PR-5.6).
    /// </summary>
    /// <remarks>
    /// The codes are provisional, so the fallback carries the raw value rather than
    /// guessing at the nearest one.
    /// </remarks>
    internal static string? DescribeServerMode(int? raw) => raw switch
    {
        null => null,
        0 => "Offline",
        1 => "Quiescent",
        2 => "Recovery",
        3 => "Backup",
        4 => "Shutdown",
        5 => "Online",
        _ => $"Unknown ({raw})",
    };

    /// <summary>
    /// A Unix epoch second count as a moment in time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the mechanism by which the whole slice avoids the INTERVAL trap: every
    /// duration IMS shows is computed here from a number, because asking the server for a
    /// duration would mean a column this driver cannot read.
    /// </para>
    /// <para>
    /// Zero is treated as unknown rather than as 1970. A server that has not recorded a
    /// timestamp reports zero, and "connected since 1 January 1970" is the kind of visible
    /// absurdity that costs a user their trust in every other number on the screen.
    /// </para>
    /// </remarks>
    internal static DateTimeOffset? FromUnixSeconds(long? epoch) => epoch switch
    {
        null or <= 0 => null,
        _ => DateTimeOffset.FromUnixTimeSeconds(epoch.Value).ToLocalTime(),
    };

    /// <summary>
    /// A cache hit rate as a percentage (PR-5.6).
    /// </summary>
    /// <remarks>
    /// Null rather than a number when the total is zero or absent. A freshly booted
    /// instance has done no reads, and reporting 0% efficiency for that would be a
    /// confident wrong answer where "unknown" is the true one.
    /// </remarks>
    internal static double? ComputeCacheRatio(long? logical, long? physical)
    {
        if (logical is null or <= 0 || physical is null or < 0 || physical > logical)
        {
            return null;
        }

        return (1.0 - ((double)physical.Value / logical.Value)) * 100.0;
    }

    /// <summary>
    /// Whether two lock modes conflict — the test that turns contention into blocking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only shared modes coexist. Anything exclusive conflicts with anything else, and an
    /// update mode conflicts with everything but a plain read.
    /// </para>
    /// <para>
    /// An unrecognised mode is treated as <em>not</em> conflicting, and that direction is
    /// deliberate: it downgrades the answer to contention rather than announcing a blocker
    /// IMS cannot justify. The cost of being wrong in the other direction is that someone
    /// interrupts a colleague's work on IMS's word.
    /// </para>
    /// </remarks>
    internal static bool AreIncompatibleLocks(string? holder, string? waiter)
    {
        static string? Normalise(string? type) => type?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "S" or "B" or "IS" => "S",
            "X" or "IX" or "SIX" => "X",
            "U" => "U",
            _ => null,
        };

        string? a = Normalise(holder);
        string? b = Normalise(waiter);

        if (a is null || b is null)
        {
            return false;
        }

        return !(a == "S" && b == "S");
    }

    // ---- Small helpers ----------------------------------------------------------

    /// <summary>
    /// Trims a CHAR column's padding, and turns an empty result into null.
    /// </summary>
    /// <remarks>
    /// Every CHAR column out of <c>sysmaster</c> arrives padded to its declared width. An
    /// untrimmed comparison is not a cosmetic problem: an untrimmed <c>idxtype</c> once
    /// reported every index in the database as non-unique.
    /// </remarks>
    private static string? Trim(string? value)
    {
        string? trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>A database-qualified object name, or whatever part of one is known.</summary>
    private static string? Qualify(string? database, string? table) =>
        (database, table) switch
        {
            (not null, not null) => $"{database}:{table}",
            (_, not null) => table,
            _ => null,
        };

    /// <summary>The server's own words about a failure, for a message the user will read.</summary>
    private static string Describe(OdbcException ex) =>
        ex.Errors.Count > 0
            ? $"{ex.Errors[0].SQLState} {ex.Errors[0].NativeError}: {ex.Errors[0].Message.Trim()}"
            : ex.Message.Trim();
}
