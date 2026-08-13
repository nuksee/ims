namespace Ims.Data.Informix.Catalog;

/// <summary>
/// The <c>sysmaster</c> queries behind the session monitor, as text.
/// </summary>
/// <remarks>
/// <para>
/// Kept together and as constants for the reasons <see cref="CatalogQueries"/> gives, and
/// one more that is specific to this slice: these are the queries a DBA is most likely to
/// want to audit before letting IMS near production, and PR-8.2 puts every one of them on
/// show in the UI. A query the user can be shown should be a query a developer can read.
/// </para>
///
/// <para><strong>Two rules, and they are not style preferences.</strong></para>
///
/// <para>
/// <em>1. Every query is bounded before it is sent.</em> <c>OdbcCommand.Cancel()</c> does
/// not reach this server — measured on 2026-08-06 against 14.10, and
/// <c>SQL_ATTR_ASYNC_ENABLE</c> is not implemented by the driver either. So a cancellation
/// token stops IMS <em>waiting</em> and the statement runs on to completion regardless.
/// The bound therefore has to be inside the statement: <c>FIRST n</c> plus a short
/// <see cref="TimeoutSeconds"/>. RSK-5 states it as bounded before it is sent, not merely
/// cancelled once running, and on this estate the test database shares a server with
/// production (DEP-2), so there is no margin for a runaway.
/// </para>
///
/// <para>
/// <em>2. No query here selects an INTERVAL column.</em>
/// <see cref="OdbcStatementResult"/> records the measurement:
/// <c>System.Data.Odbc</c> has no type-map entry for ODBC's <c>SQL_INTERVAL_*</c> types,
/// so <c>GetValue</c>, <c>IsDBNull</c>, <c>GetFieldType</c> and <c>GetSchemaTable</c> all
/// throw <c>ArgumentException</c> from inside the type map — and the damage is not confined
/// to the offending column, because every column at or after the first INTERVAL becomes
/// unreadable too. The catalogue reader's ordinal helpers all route through
/// <c>GetValue</c>/<c>IsDBNull</c>, so they are not safe on one.
/// </para>
/// <para>
/// Durations are the natural shape of half of what a session monitor wants — how long
/// connected, how long waiting, how long since a checkpoint — which makes this the sharpest
/// risk in the slice. So durations are always either an epoch or a count read as a number
/// and converted client-side, or <c>CAST(... AS CHAR(n))</c> read as text. Never a server
/// computed duration.
/// </para>
/// <para>
/// And because a column's type can only be confirmed against a live server: <strong>any
/// column whose type is uncertain goes last in the select list</strong>, so that a surprise
/// INTERVAL costs one column rather than the whole tail. The ordering of these select
/// lists is deliberate, not cosmetic.
/// </para>
/// </remarks>
internal static class SessionQueries
{
    /// <summary>
    /// The row cap on the session list.
    /// </summary>
    /// <remarks>
    /// Well past any session count these instances see, and still small enough that the
    /// worst case is bounded. When the cap is reached the UI says so rather than presenting
    /// a partial list as complete — and the session <em>count</em> comes from
    /// <see cref="SessionCount"/>, which stays true whatever the list does.
    /// </remarks>
    public const int RowCap = 500;

    /// <summary>The cap on per-session lock reads, which are far smaller.</summary>
    public const int LockCap = 200;

    /// <summary>
    /// Seconds. Shorter than the catalogue's 60 on purpose.
    /// </summary>
    /// <remarks>
    /// A monitor refresh that has not answered in ten seconds has already failed what it
    /// was for (NFR-1), and because the cancel does not reach the server, the timeout is
    /// the only thing that actually ends the statement.
    /// </remarks>
    public const int TimeoutSeconds = 10;

    /// <summary>
    /// The session list (PR-5.1). <c>onstat -g ses</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on <c>syssessions</c> rather than on the <c>sysscblst</c>/<c>sysrstcb</c>/
    /// <c>systcblst</c> join that reproduces <c>onstat -g ses</c> in full. That join is the
    /// least stable shape in <c>sysmaster</c>, and <c>syssessions</c> is the one object here
    /// with a measured success against 14.10: the Slice 0 smoke test read <c>sid</c> and
    /// <c>username</c> from it as an ordinary developer, which is what answered Q-1 and
    /// unblocked this slice. Putting PR-5.1's core on the proven object and treating the
    /// rest as enrichment is the difference between a list that degrades and a list that
    /// fails.
    /// </para>
    /// <para>
    /// Column order matters here, per rule 2. <c>sid</c> and <c>username</c> are confirmed,
    /// so they lead. <c>connected</c> is last because it is the one column most likely to
    /// be a type this driver cannot read: it should be an epoch integer, and if it turns
    /// out to be an INTERVAL then only it is lost rather than everything after it.
    /// </para>
    /// </remarks>
    public static readonly string SessionList = $"""
        SELECT FIRST {RowCap}
               sid,
               username,
               hostname,
               pid,
               feprogram,
               state,
               connected
          FROM sysmaster:syssessions
         ORDER BY sid
        """;

    /// <summary>
    /// The session list without the columns that are least certain to exist.
    /// </summary>
    /// <remarks>
    /// The fallback for a server that refuses <see cref="SessionList"/>. PR-5.1 names the
    /// application and the state, so losing them is a real reduction and the UI says so —
    /// but a list of who is connected is worth far more than an error where the list should
    /// be, and NFR-4 asks for degradation rather than opaque failure.
    /// </remarks>
    public static readonly string SessionListMinimal = $"""
        SELECT FIRST {RowCap}
               sid,
               username,
               hostname
          FROM sysmaster:syssessions
         ORDER BY sid
        """;

    /// <summary>
    /// How many sessions there are, whatever the list cap did (PR-5.6).
    /// </summary>
    /// <remarks>
    /// Its own query rather than the length of the list, so a capped list still reports a
    /// true count. One aggregate over a view IMS has already read successfully is about as
    /// cheap as a question gets.
    /// </remarks>
    public const string SessionCount = """
        SELECT COUNT(*) FROM sysmaster:syssessions
        """;

    /// <summary>
    /// What one session is running (PR-5.1). <c>onstat -g sql</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the session id and read only for the selected session, never for all 500.
    /// That serves PR-6.4 — the statement text is the largest thing in this slice and
    /// fetching it per row would be the one query here that is not negligible — and it also
    /// contains the risk, because the column names below are the least certain in the file.
    /// If this table is not shaped as expected, one pane section says so and the list is
    /// untouched.
    /// </para>
    /// <para>
    /// The statement is truncated server-side rather than shipped whole. PR-8.2 forbids
    /// hiding the server, so the truncation is reported to the user rather than performed
    /// silently — see <c>SessionDetail.CurrentSqlTruncated</c>.
    /// </para>
    /// </remarks>
    public static readonly string CurrentSql = $"""
        SELECT FIRST 1
               sqx_sessionid,
               SUBSTR(sqx_statement, 1, {CurrentSqlLength}) AS sqx_statement
          FROM sysmaster:syssqlcurses
         WHERE sqx_sessionid = ?
        """;

    /// <summary>How much of a statement the detail pane shows.</summary>
    public const int CurrentSqlLength = 2000;

    /// <summary>
    /// The locks one session holds (PR-5.2). <c>onstat -g lok</c>.
    /// </summary>
    /// <remarks>
    /// <c>syslocks</c> is a view and the friendliest lock object in <c>sysmaster</c>. Note
    /// that <c>owner</c> is assumed to be a session id; if it is a process id instead, this
    /// keys on the wrong number and would attribute locks to the wrong session. That is the
    /// worst failure mode in this slice, which is why the blocker it feeds is graded rather
    /// than asserted — see <c>LockWaitFidelity</c>. No INTERVAL risk in this query at all.
    /// </remarks>
    public static readonly string LocksHeld = $"""
        SELECT FIRST {LockCap}
               owner, dbsname, tabname, rowidlk, keynum, type
          FROM sysmaster:syslocks
         WHERE owner = ?
         ORDER BY dbsname, tabname
        """;

    /// <summary>
    /// Who is waiting on whom (PR-5.3). <c>onstat -g lok</c>, <c>onstat -K</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>syslocks.waiter</c> is the session waiting for the lock the row describes, so a
    /// single scan gives the waiter/holder pair outright. Rows where it is null are locks
    /// nobody is queued behind, which is most of them on a healthy instance.
    /// </para>
    /// <para>
    /// <strong>This replaced a self-join, and the reason is measured.</strong> The first
    /// version joined <c>syslocks</c> to itself on lock identity — database, table, row, key
    /// — to find sessions contending on one resource. Run against 14.10.FC10W2X7 on
    /// 2026-08-13 it timed out repeatedly at the 10-second cap: <c>HYT00 Timeout expired</c>,
    /// every time. <c>syslocks</c> is a pseudo-table over shared memory with no indexes, so
    /// the join is quadratic in the lock count and there is nothing to make it cheaper. It
    /// was never going to fit a monitor's budget (PR-6.4), and a query that only completes on
    /// an idle instance is no use on the busy one where a blocked session actually matters.
    /// </para>
    /// <para>
    /// The lock modes still decide whether a wait is a <em>block</em>, and that stays a pure
    /// client-side function rather than a predicate here: it is a rule worth testing, and it
    /// is where an unrecognised mode is deliberately read as "not blocking" so IMS downgrades
    /// its claim rather than naming a blocker it cannot justify.
    /// </para>
    /// </remarks>
    public static readonly string LockWaits = $"""
        SELECT FIRST {LockCap}
               waiter, owner, dbsname, tabname, rowidlk, keynum, type
          FROM sysmaster:syslocks
         WHERE waiter IS NOT NULL
        """;

    /// <summary>
    /// The self-join fallback, for a server whose <c>syslocks</c> has no <c>waiter</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Finds sessions holding locks on the same resource, which is a <em>superset</em> of one
    /// blocking the other — two shared locks match it and block nothing. So its rows are
    /// reported as contention, never as an identified blocker.
    /// </para>
    /// <para>
    /// Measured to time out at ten seconds against 14.10 (see <see cref="LockWaits"/>), so it
    /// is a last resort and capped far harder. It is kept only because losing lock waits
    /// entirely on a server without the <c>waiter</c> column would be worse than one slow
    /// query that usually fails — and when it does fail, the fidelity says Unknown and the
    /// pane says so.
    /// </para>
    /// </remarks>
    public static readonly string LockContention = $"""
        SELECT FIRST {ContentionCap}
               w.owner AS waiter_sid,
               h.owner AS holder_sid,
               w.dbsname, w.tabname, w.rowidlk, w.keynum,
               w.type AS waiter_type,
               h.type AS holder_type
          FROM sysmaster:syslocks w, sysmaster:syslocks h
         WHERE w.dbsname = h.dbsname
           AND w.tabname = h.tabname
           AND w.rowidlk = h.rowidlk
           AND w.keynum = h.keynum
           AND w.owner <> h.owner
        """;

    /// <summary>
    /// The cap on the contention fallback, well below <see cref="LockCap"/>.
    /// </summary>
    /// <remarks>
    /// The join is quadratic in the lock count, so the cap is the only thing bounding it —
    /// and a cancel would not reach the server to stop it.
    /// </remarks>
    public const int ContentionCap = 50;

    /// <summary>
    /// What one session is consuming (PR-5.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>sysrstcb</c> is the RSAM thread control block and its shape is the least stable in
    /// <c>sysmaster</c>, which is exactly why nothing else in this slice depends on it. The
    /// whole read is one isolated section, so PR-5.2 lands partly met rather than falsely
    /// complete when it fails.
    /// </para>
    /// <para>
    /// <strong>Not sent, and that is the current state of PR-5.2's resource half.</strong> The
    /// column names here were guesses, and 14.10.FC10W2X7 rejected them one at a time:
    /// <c>dbnum</c> on 2026-08-13, then <c>memtotal</c> on the very next run once <c>dbnum</c>
    /// was removed. Guessing again would cost another round trip per session click to learn the
    /// same thing about the next name in the list.
    /// </para>
    /// <para>
    /// So the query is kept — it is still shown under PR-8.2, marked as not attempted, so the
    /// user can see what IMS would ask and why it does not — but
    /// <see cref="ResourceColumnsAreVerified"/> gates it off until someone runs
    /// <c>--probe-sessions</c> and puts real names here. An honest gap beats a query that exists
    /// only to fail.
    /// </para>
    /// </remarks>
    public const string SessionResources = """
        SELECT FIRST 1
               sid, memtotal, memused
          FROM sysmaster:sysrstcb
         WHERE sid = ?
        """;

    /// <summary>
    /// Whether <see cref="SessionResources"/>' column names have been confirmed against a server.
    /// </summary>
    /// <remarks>
    /// False, deliberately, and a constant rather than a setting: it is a fact about what this
    /// code knows, not a preference. Set it true in the same commit that corrects the names, and
    /// only on the strength of a probe run — not on the strength of documentation.
    /// </remarks>
    public const bool ResourceColumnsAreVerified = false;

    /// <summary>
    /// The instance's mode and boot time (PR-5.6).
    /// </summary>
    /// <remarks>
    /// The boot time is read as an epoch integer and the uptime computed client-side. Any
    /// view offering uptime directly would offer it as a duration, and a duration is an
    /// INTERVAL — see rule 2. This is the clearest case in the slice of the rule earning
    /// its place.
    /// </remarks>
    public const string ServerState = """
        SELECT FIRST 1
               sh_mode, sh_boottime
          FROM sysmaster:sysshmvals
        """;

    /// <summary>
    /// The counters behind buffer efficiency (PR-5.6).
    /// </summary>
    /// <remarks>
    /// <c>sysprofile</c> is a name/value table, so this is one small keyed read rather than
    /// a scan. The ratios are computed client-side, where a zero denominator on a
    /// freshly-booted instance can be reported as unknown instead of dividing by zero.
    /// <c>name</c> is CHAR and padded — it must be trimmed before it is matched.
    /// </remarks>
    public const string Profile = """
        SELECT FIRST 40 name, value
          FROM sysmaster:sysprofile
         WHERE name IN ('dskreads', 'bufreads', 'dskwrits', 'bufwrits')
        """;

    /// <summary>
    /// When the instance last checkpointed (PR-5.6).
    /// </summary>
    /// <remarks>
    /// An epoch integer again, converted client-side. <c>sysprofile.numckpts</c> is a
    /// <em>count</em> of checkpoints and does not answer recency, so it is not a fallback
    /// for this: if this object is absent, checkpoint recency is unknown and the strip
    /// omits it.
    /// </remarks>
    public const string LastCheckpoint = """
        SELECT FIRST 1 MAX(ckpt_time) FROM sysmaster:syscheckpoint
        """;
}
