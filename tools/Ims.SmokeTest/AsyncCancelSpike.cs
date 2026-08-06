using System.Data.Odbc;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ims.SmokeTest;

/// <summary>
/// Finds out whether asynchronous execution makes <c>SQLCancel</c> work, before
/// anyone builds the expensive alternative.
/// </summary>
/// <remarks>
/// <para>
/// Measured 2026-08-06: <c>OdbcCommand.Cancel()</c> does not stop a running
/// statement on 14.10, on either a sorting or a scanning workload. The
/// remaining cheap explanation is that <c>System.Data.Odbc</c> executes
/// synchronously, and ODBC only promises <c>SQLCancel</c> will interrupt a
/// statement that is executing asynchronously or is waiting on data-at-execution.
/// Against a synchronous handle it is permitted to do nothing, which is exactly
/// what was observed.
/// </para>
/// <para>
/// If setting <c>SQL_ATTR_ASYNC_ENABLE</c> makes the cancel land, PR-3.5 is
/// reachable over the existing single connection and the fix is contained. If it
/// does not, the fallback is a second connection issuing an administrative cancel
/// — which costs the extra session PR-6.4 asks IMS not to add, so it is worth
/// this spike to avoid guessing.
/// </para>
/// <para>
/// <strong>This is spike code and does not belong in IMS as written.</strong> It
/// reaches the driver handle by reflection over <c>System.Data.Odbc</c> internals,
/// which is unsupported and version-fragile. It lives here because the question is
/// worth answering cheaply; a real implementation would need a supported route.
/// </para>
/// </remarks>
internal static class AsyncCancelSpike
{
    // ODBC handle types and attributes, from sql.h / sqlext.h.
    private const short SQL_HANDLE_DBC = 2;
    private const int SQL_ATTR_ASYNC_ENABLE = 4;
    private const int SQL_ASYNC_ENABLE_ON = 1;
    private const int SQL_IS_UINTEGER = -5;

    private const short SQL_SUCCESS = 0;
    private const short SQL_SUCCESS_WITH_INFO = 1;

    [DllImport("odbc32.dll", CharSet = CharSet.Unicode)]
    private static extern short SQLSetConnectAttrW(
        IntPtr connectionHandle,
        int attribute,
        IntPtr value,
        int stringLength);

    [DllImport("odbc32.dll", CharSet = CharSet.Unicode)]
    private static extern short SQLGetConnectAttrW(
        IntPtr connectionHandle,
        int attribute,
        out IntPtr value,
        int bufferLength,
        IntPtr stringLengthPtr);

    [DllImport("odbc32.dll", CharSet = CharSet.Unicode)]
    private static extern short SQLGetDiagRecW(
        short handleType,
        IntPtr handle,
        short recordNumber,
        [Out] char[] sqlState,
        out int nativeError,
        [Out] char[] messageText,
        short bufferLength,
        out short textLength);

    /// <summary>
    /// Reads the driver's own explanation for the last failure on a handle.
    /// </summary>
    /// <remarks>
    /// A bare return code says something failed but not why, and the difference
    /// between "this driver does not support async" and "that attribute is not valid
    /// here" decides whether the route is closed or merely misused.
    /// </remarks>
    private static string ReadDiagnostics(IntPtr dbc)
    {
        var messages = new List<string>();

        for (short record = 1; record <= 5; record++)
        {
            char[] state = new char[6];
            char[] text = new char[1024];

            short rc = SQLGetDiagRecW(
                SQL_HANDLE_DBC, dbc, record, state, out int native, text, (short)text.Length, out short len);

            if (rc is not (SQL_SUCCESS or SQL_SUCCESS_WITH_INFO))
            {
                break;
            }

            string sqlState = new string(state).TrimEnd('\0');
            string message = new string(text, 0, Math.Clamp(len, 0, text.Length)).Trim();

            messages.Add(FormattableString.Invariant(
                $"SQLSTATE {sqlState}, native {native}: {message}"));
        }

        return messages.Count == 0
            ? "the driver gave no diagnostic"
            : string.Join(" | ", messages);
    }

    /// <summary>
    /// Turns async execution on for the connection, runs a slow statement, cancels
    /// it, and reports whether the cancel actually landed.
    /// </summary>
    public static async Task<ProbeResult> RunAsync(
        OdbcConnection connection,
        SmokeTestOptions options,
        CancellationToken cancellationToken)
    {
        const string name = "Cancel via SQL_ATTR_ASYNC_ENABLE";

        if (!options.AnyLoadProbes)
        {
            return ProbeResult.Skip(
                name,
                "PR-3.5",
                "Needs --include-light-load (bounded) or --include-load (unbounded).");
        }

        if (!TryGetConnectionHandle(connection, out IntPtr dbc, out string handleDetail))
        {
            return ProbeResult.Inconclusive(
                name,
                "PR-3.5",
                "Could not reach the ODBC connection handle, so the attribute could not "
                + $"be set and this says nothing either way. {handleDetail} The spike "
                + "reads System.Data.Odbc internals by reflection, which a runtime update "
                + "is free to break.",
                statement: null);
        }

        short set = SQLSetConnectAttrW(
            dbc,
            SQL_ATTR_ASYNC_ENABLE,
            new IntPtr(SQL_ASYNC_ENABLE_ON),
            SQL_IS_UINTEGER);

        if (set is not (SQL_SUCCESS or SQL_SUCCESS_WITH_INFO))
        {
            // A driver that refuses the attribute is a real answer: this route is
            // closed and the fallback is the only remaining option.
            return ProbeResult.Fail(
                name,
                "PR-3.5",
                $"The driver rejected SQL_ATTR_ASYNC_ENABLE (SQLSetConnectAttr returned {set}): "
                + ReadDiagnostics(dbc) + ". Asynchronous execution is not available on this "
                + "connection, so it cannot be what makes Cancel() work. The second-connection "
                + "administrative cancel is the remaining option — weigh it against PR-6.4.",
                statement: null);
        }

        // Confirm it stuck rather than being silently accepted and ignored, which is
        // the same failure mode SECURITY=ssl had.
        bool readbackWorked =
            SQLGetConnectAttrW(dbc, SQL_ATTR_ASYNC_ENABLE, out IntPtr current, 0, IntPtr.Zero)
                is SQL_SUCCESS or SQL_SUCCESS_WITH_INFO;

        bool asyncActuallyOn = readbackWorked && current.ToInt64() == SQL_ASYNC_ENABLE_ON;

        string readback = readbackWorked
            ? FormattableString.Invariant($"read back as {current.ToInt64()}")
            : "could not be read back";

        try
        {
            // The same scanning workload the synchronous probe used, so the two
            // results are comparable. Sorting is not involved; that was ruled out.
            bool bounded = !options.IncludeLoadProbes;
            string sql = bounded
                ? "SELECT COUNT(*) FROM systables a, systables b, systables c WHERE a.tabid + b.tabid + c.tabid < 0"
                : "SELECT COUNT(*) FROM systables a, systables b, systables c, systables d WHERE a.tabid + b.tabid + c.tabid + d.tabid < 0";

            using var command = new OdbcCommand(sql, connection);
            command.CommandTimeout = bounded ? 30 : 0;

            var stopwatch = Stopwatch.StartNew();
            Task execute = command.ExecuteScalarAsync(cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            command.Cancel();

            bool timedOut = false;
            string outcome;

            try
            {
                await execute.ConfigureAwait(false);
                outcome = "the statement completed before the cancel landed";
            }
            catch (OdbcException ex)
            {
                timedOut =
                    ex.Errors.Cast<OdbcError>().Any(e =>
                        string.Equals(e.SQLState, "HYT00", StringComparison.OrdinalIgnoreCase))
                    || (command.CommandTimeout > 0
                        && stopwatch.Elapsed.TotalSeconds >= command.CommandTimeout - 1);

                outcome = timedOut
                    ? $"ran to the {command.CommandTimeout}s timeout anyway "
                      + $"({stopwatch.ElapsedMilliseconds} ms): {Probes.Describe(ex)}"
                    : $"cancelled after {stopwatch.ElapsedMilliseconds} ms: {Probes.Describe(ex)}";
            }
            catch (OperationCanceledException)
            {
                outcome = $"cancelled after {stopwatch.ElapsedMilliseconds} ms";
            }

            string context =
                $" Async enable {readback}. The synchronous baseline for this identical "
                + "statement is the Cancellation (scan) probe, measured on 2026-08-06 as "
                + "running to its timeout uncancelled; pass --recheck-cancellation to "
                + "re-measure it alongside this one.";

            if (timedOut)
            {
                return ProbeResult.Fail(
                    name,
                    "PR-3.5",
                    $"Async execution did not help: {outcome}." + context
                    + " Both routes over one connection are now exhausted, so PR-3.5 needs the "
                    + "second-connection administrative cancel — which costs the extra session "
                    + "PR-6.4 asks IMS not to add. That trade-off is now a decision, not a "
                    + "hypothetical.",
                    sql);
            }

            if (outcome.StartsWith("the statement completed", StringComparison.Ordinal))
            {
                return ProbeResult.Inconclusive(
                    name,
                    "PR-3.5",
                    $"{outcome}, so Cancel() was never tested here." + context
                    + " Make the statement slower and re-run.",
                    sql);
            }

            return ProbeResult.Pass(
                name,
                "PR-3.5",
                $"Cancel() landed with async execution enabled: {outcome}." + context
                + (asyncActuallyOn
                    ? " PR-3.5 is reachable over the existing single connection — no second "
                      + "session needed, so PR-6.4 stays intact. Note IMS would have to set "
                      + "this attribute through a supported route, not the reflection this "
                      + "spike uses."
                    : " Treat with caution: the attribute did not read back as ON, so the "
                      + "cancel may have landed for some other reason."),
                sql);
        }
        finally
        {
            // Leave the connection as it was found. Later probes share it, and an
            // async handle changes how every subsequent call behaves.
            SQLSetConnectAttrW(dbc, SQL_ATTR_ASYNC_ENABLE, IntPtr.Zero, SQL_IS_UINTEGER);
        }
    }

    /// <summary>
    /// Digs the native connection handle out of <see cref="OdbcConnection"/>.
    /// </summary>
    /// <remarks>
    /// Unsupported by design: the handle is deliberately private, and the field names
    /// walked here are implementation detail of System.Data.Odbc. Acceptable in a
    /// spike whose entire purpose is to answer one question before committing to an
    /// expensive design; not acceptable in IMS.
    /// </remarks>
    private static bool TryGetConnectionHandle(
        OdbcConnection connection,
        out IntPtr handle,
        out string detail)
    {
        handle = IntPtr.Zero;
        detail = string.Empty;

        try
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            // The handle hangs off OdbcConnection itself, not off InnerConnection —
            // OdbcConnectionOpen has no property for it, which an earlier version of
            // this spike assumed and would have failed on. Verified by enumerating the
            // type: OdbcConnection has an internal ConnectionHandle property backed by
            // the _connectionHandle field, and it is only populated while open.
            object? connectionHandle =
                typeof(OdbcConnection).GetProperty("ConnectionHandle", flags)?.GetValue(connection)
                ?? typeof(OdbcConnection).GetField("_connectionHandle", flags)?.GetValue(connection);

            if (connectionHandle is null)
            {
                detail =
                    "Neither OdbcConnection.ConnectionHandle nor _connectionHandle was found "
                    + "or set. The connection must be open when this runs.";
                return false;
            }

            // OdbcConnectionHandle derives from OdbcHandle, which derives from SafeHandle.
            if (connectionHandle is SafeHandle safe)
            {
                if (safe.IsInvalid || safe.IsClosed)
                {
                    detail = "The connection handle is closed or invalid.";
                    return false;
                }

                handle = safe.DangerousGetHandle();
                return handle != IntPtr.Zero;
            }

            detail = $"ConnectionHandle was {connectionHandle.GetType().Name}, not a SafeHandle.";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>The ODBC handle type for a connection, kept for readers of the P/Invokes.</summary>
    internal static short ConnectionHandleType => SQL_HANDLE_DBC;
}
