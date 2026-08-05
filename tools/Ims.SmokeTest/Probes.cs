using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Globalization;
using Ims.Core.Data;
using Ims.Data.Informix;

namespace Ims.SmokeTest;

/// <summary>
/// The probes that answer the Slice 0 spike questions.
/// </summary>
/// <remarks>
/// <para>
/// These use <see cref="OdbcConnection"/> directly rather than going through
/// <c>IInformixSession</c>, on purpose. The question a spike answers is "what does
/// the provider actually do", and an abstraction in the way would only make that
/// harder to see. The Slice 0 helpers that <em>are</em> exercised — CsdkLocator,
/// InformixOdbcConnectionString, InformixTypeMapper — are the ones whose behaviour
/// against a real server is still unknown.
/// </para>
/// <para>
/// Every statement sent is printed with its result (PR-6.2, PR-8.2). Nothing here
/// writes to the database.
/// </para>
/// </remarks>
public static class Probes
{
    public static async Task<IReadOnlyList<ProbeResult>> RunAllAsync(
        SmokeTestOptions options,
        CsdkDetectionResult csdk,
        CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>();

        string connectionString = InformixOdbcConnectionString.Build(
            options.ToDescriptor(),
            csdk.OdbcDriverName!,
            options.Password);

        OdbcConnection? connection = null;

        try
        {
            (ProbeResult connectResult, connection) =
                await ConnectAsync(connectionString, cancellationToken).ConfigureAwait(false);

            results.Add(connectResult);

            if (connection is null)
            {
                results.Add(ProbeResult.Skip("Version banner", "RSK-9", "No connection."));
                results.Add(ProbeResult.Skip("Error detail", "PR-3.6", "No connection."));
                results.Add(ProbeResult.Skip("Type fidelity", "PR-4.5", "No connection."));
                results.Add(ProbeResult.Skip("Streaming", "PR-4.2", "No connection."));
                results.Add(ProbeResult.Skip("Cancellation", "PR-3.5", "No connection."));
                results.Add(ProbeResult.Skip("sysmaster readable", "Q-1 / AS-3", "No connection."));
                return results;
            }

            results.Add(await VersionAsync(connection, cancellationToken).ConfigureAwait(false));
            results.Add(await ErrorDetailAsync(connection, cancellationToken).ConfigureAwait(false));
            results.Add(await TypeFidelityAsync(connection, cancellationToken).ConfigureAwait(false));
            results.Add(await StreamingAsync(connection, options, cancellationToken).ConfigureAwait(false));
            results.Add(await CancellationAsync(connection, options, cancellationToken).ConfigureAwait(false));
            results.Add(await SysMasterAsync(connection, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        return results;
    }

    private static async Task<(ProbeResult Result, OdbcConnection? Connection)> ConnectAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new OdbcConnection(connectionString);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return (
                ProbeResult.Pass(
                    "Connect",
                    "PR-1.1 / DEC-4",
                    $"Connected in {stopwatch.ElapsedMilliseconds} ms over the CSDK ODBC driver."),
                connection);
        }
        catch (OdbcException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            return (
                ProbeResult.Fail(
                    "Connect",
                    "PR-1.1 / DEC-4",
                    Describe(ex) + $"  [connection string: {InformixOdbcConnectionString.ForLogging(connectionString)}]"),
                null);
        }
    }

    private static async Task<ProbeResult> VersionAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT FIRST 1 DBINFO('version', 'full') FROM systables";

        try
        {
            object? banner = await ScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false);

            return ProbeResult.Pass(
                "Version banner",
                "RSK-9 / NFR-4",
                banner?.ToString() ?? "(empty)",
                sql);
        }
        catch (OdbcException ex)
        {
            return ProbeResult.Fail("Version banner", "RSK-9 / NFR-4", Describe(ex), sql);
        }
    }

    /// <summary>
    /// PR-3.6 wants the Informix error code, the ISAM error and an explanation
    /// together. This finds out how much of that ODBC actually surfaces.
    /// </summary>
    private static async Task<ProbeResult> ErrorDetailAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        // A table that cannot exist. Deliberately a read, so nothing is changed.
        const string sql = "SELECT * FROM ims_smoke_test_no_such_table";

        try
        {
            await ScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false);

            return ProbeResult.Inconclusive(
                "Error detail",
                "PR-3.6",
                "Expected the statement to fail, and it did not.",
                sql);
        }
        catch (OdbcException ex)
        {
            var detail = new List<string>();
            var sawNativeCode = false;

            foreach (OdbcError error in ex.Errors)
            {
                detail.Add(
                    $"SQLSTATE {error.SQLState}, native {error.NativeError.ToString(CultureInfo.InvariantCulture)}: "
                    + error.Message.Trim());

                if (error.NativeError != 0)
                {
                    sawNativeCode = true;
                }
            }

            // Informix reports the SQLCODE and the ISAM error as two entries in the
            // diagnostic record. If only one comes through, PR-3.6 needs another route.
            string summary = string.Join(" | ", detail);

            return ex.Errors.Count >= 2 && sawNativeCode
                ? ProbeResult.Pass(
                    "Error detail",
                    "PR-3.6",
                    $"{ex.Errors.Count} diagnostic records, SQLCODE and ISAM both present. {summary}",
                    sql)
                : ProbeResult.Inconclusive(
                    "Error detail",
                    "PR-3.6",
                    $"{ex.Errors.Count} diagnostic record(s). PR-3.6 wants SQLCODE and the ISAM "
                    + $"error together — check whether the ISAM code is here. {summary}",
                    sql);
        }
    }

    /// <summary>
    /// PR-4.5, and the probe that most decides whether the ODBC branch of DEC-4 was
    /// the right call. Reports what CLR type each Informix type arrives as.
    /// </summary>
    private static async Task<ProbeResult> TypeFidelityAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FIRST 1
                CURRENT YEAR TO FRACTION(3)          AS dt_fraction3,
                CURRENT YEAR TO SECOND               AS dt_second,
                TODAY                                AS plain_date,
                INTERVAL (5 12:30:45) DAY TO SECOND  AS iv_day_second,
                INTERVAL (2-06) YEAR TO MONTH        AS iv_year_month,
                12345.67::DECIMAL(10,2)              AS dec_value,
                12345.67::MONEY(10,2)                AS money_value,
                'hello'::LVARCHAR                    AS lvarchar_value
            FROM systables
            """;

        try
        {
            using var command = new OdbcCommand(sql, connection);
            command.CommandTimeout = 30;

            using OdbcDataReader reader = (OdbcDataReader)await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ProbeResult.Inconclusive("Type fidelity", "PR-4.5", "No row returned.", sql);
            }

            var lines = new List<string>();

            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                string serverType = reader.GetDataTypeName(i);
                object? raw = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);

                InformixDbType mapped = InformixTypeMapper.FromServerTypeName(serverType);

                lines.Add(
                    $"{name}: server='{serverType}' clr={raw?.GetType().Name ?? "null"} "
                    + $"mapped={mapped} value='{raw}'");
            }

            return ProbeResult.Inconclusive(
                "Type fidelity",
                "PR-4.5",
                "Read the mapping below and confirm DATETIME keeps its qualifier and "
                + "INTERVAL arrives in a parseable form:" + Environment.NewLine
                + string.Join(Environment.NewLine, lines.Select(l => "      " + l)),
                sql);
        }
        catch (OdbcException ex)
        {
            return ProbeResult.Fail("Type fidelity", "PR-4.5", Describe(ex), sql);
        }
    }

    /// <summary>
    /// PR-4.2 and RSK-6: does the driver stream, or does it buffer the whole result
    /// set before handing over the first row?
    /// </summary>
    private static async Task<ProbeResult> StreamingAsync(
        OdbcConnection connection,
        SmokeTestOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.IncludeLoadProbes)
        {
            return ProbeResult.Skip(
                "Streaming",
                "PR-4.2 / RSK-6",
                "Needs --include-load: it reads a large result set (RSK-5, PR-6.4).");
        }

        const string sql = "SELECT a.tabname, b.tabname FROM systables a, systables b";

        try
        {
            using var command = new OdbcCommand(sql, connection);
            command.CommandTimeout = 120;

            long before = GC.GetTotalMemory(forceFullCollection: true);
            var stopwatch = Stopwatch.StartNew();

            using OdbcDataReader reader = (OdbcDataReader)await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            long firstRowMs = stopwatch.ElapsedMilliseconds;
            long rows = 0;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows++;

                if (rows >= 200_000)
                {
                    break;
                }
            }

            stopwatch.Stop();
            long after = GC.GetTotalMemory(forceFullCollection: false);

            // If the driver buffers, time-to-first-row is close to total time and
            // memory climbs with the row count. If it streams, the first row arrives
            // fast and memory stays flat.
            string detail =
                $"{rows} rows in {stopwatch.ElapsedMilliseconds} ms, "
                + $"first row after {firstRowMs} ms, "
                + $"managed heap {(after - before) / 1024 / 1024} MB.";

            return firstRowMs < stopwatch.ElapsedMilliseconds / 2
                ? ProbeResult.Pass("Streaming", "PR-4.2 / RSK-6", "Appears to stream. " + detail, sql)
                : ProbeResult.Inconclusive(
                    "Streaming",
                    "PR-4.2 / RSK-6",
                    "Time-to-first-row is close to total time, which suggests buffering. " + detail,
                    sql);
        }
        catch (OdbcException ex)
        {
            return ProbeResult.Fail("Streaming", "PR-4.2 / RSK-6", Describe(ex), sql);
        }
    }

    /// <summary>
    /// PR-3.5, and the one that most constrains Slice 1's design: can a running
    /// statement be cancelled without losing the session?
    /// </summary>
    private static async Task<ProbeResult> CancellationAsync(
        OdbcConnection connection,
        SmokeTestOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.IncludeLoadProbes)
        {
            return ProbeResult.Skip(
                "Cancellation",
                "PR-3.5",
                "Needs --include-load: it must start a deliberately slow statement (RSK-5).");
        }

        const string sql = "SELECT COUNT(*) FROM systables a, systables b, systables c, systables d";

        try
        {
            using var command = new OdbcCommand(sql, connection);
            command.CommandTimeout = 0;

            var stopwatch = Stopwatch.StartNew();
            Task execute = command.ExecuteScalarAsync(cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            command.Cancel();

            string outcome;
            try
            {
                await execute.ConfigureAwait(false);
                outcome = "the statement completed before the cancel landed";
            }
            catch (OdbcException ex)
            {
                outcome = $"cancelled after {stopwatch.ElapsedMilliseconds} ms ({Describe(ex)})";
            }
            catch (OperationCanceledException)
            {
                outcome = $"cancelled after {stopwatch.ElapsedMilliseconds} ms";
            }

            // The half that matters: PR-3.5 says "without terminating the session".
            bool sessionSurvived;
            string survivalDetail;

            try
            {
                object? probe = await ScalarAsync(
                    connection,
                    "SELECT FIRST 1 tabname FROM systables",
                    cancellationToken).ConfigureAwait(false);

                sessionSurvived = probe is not null;
                survivalDetail = "session still usable afterwards";
            }
            catch (OdbcException ex)
            {
                sessionSurvived = false;
                survivalDetail = "session unusable afterwards: " + Describe(ex);
            }

            return sessionSurvived
                ? ProbeResult.Pass("Cancellation", "PR-3.5", $"{outcome}; {survivalDetail}.", sql)
                : ProbeResult.Fail(
                    "Cancellation",
                    "PR-3.5",
                    $"{outcome}; {survivalDetail}. Slice 1 will need a different cancellation "
                    + "strategy — probably a second connection issuing an administrative cancel.",
                    sql);
        }
        catch (OdbcException ex)
        {
            return ProbeResult.Fail("Cancellation", "PR-3.5", Describe(ex), sql);
        }
    }

    /// <summary>
    /// Q-1, the question that gates Slice 3 entirely. Run this as an ordinary
    /// unprivileged developer, not as informix.
    /// </summary>
    private static async Task<ProbeResult> SysMasterAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT FIRST 1 sid, username FROM sysmaster:syssessions";

        try
        {
            object? sid = await ScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false);

            return ProbeResult.Pass(
                "sysmaster readable",
                "Q-1 / AS-3 / DEP-4",
                $"Read syssessions as this user (first sid {sid}). Slice 3 serves the primary user.",
                sql);
        }
        catch (OdbcException ex)
        {
            return ProbeResult.Fail(
                "sysmaster readable",
                "Q-1 / AS-3 / DEP-4",
                "Cannot read sysmaster as this user. Per the PRD, Slice 3 then serves only U2/U3 "
                + "and its priority should be reconsidered against section 8. " + Describe(ex),
                sql);
        }
    }

    private static async Task<object?> ScalarAsync(
        OdbcConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = new OdbcCommand(sql, connection);
        command.CommandTimeout = 30;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(OdbcException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return exception.Message.Trim();
        }

        OdbcError first = exception.Errors[0];

        return $"SQLSTATE {first.SQLState}, native "
               + $"{first.NativeError.ToString(CultureInfo.InvariantCulture)}: "
               + first.Message.Trim();
    }
}
