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

            // Each probe is isolated. An unexpected throw in one must not cost the
            // answers the others would have given — the first real run was aborted
            // entirely by an ArgumentException deep inside the ODBC type map, which
            // hid every subsequent result including the Q-1 answer.
            results.Add(await SafelyAsync("Version banner", "RSK-9",
                () => VersionAsync(connection, cancellationToken)).ConfigureAwait(false));

            results.Add(await SafelyAsync("Error detail", "PR-3.6",
                () => ErrorDetailAsync(connection, cancellationToken)).ConfigureAwait(false));

            results.Add(await SafelyAsync("Interval access", "PR-4.5",
                () => IntervalAccessAsync(connection, cancellationToken)).ConfigureAwait(false));

            results.Add(await SafelyAsync("Type fidelity", "PR-4.5",
                () => TypeFidelityAsync(connection, cancellationToken)).ConfigureAwait(false));

            results.Add(await SafelyAsync("Streaming", "PR-4.2",
                () => StreamingAsync(connection, options, cancellationToken)).ConfigureAwait(false));

            results.Add(await SafelyAsync("Cancellation", "PR-3.5",
                () => CancellationAsync(connection, options, cancellationToken)).ConfigureAwait(false));

            results.Add(await SafelyAsync("sysmaster readable", "Q-1 / AS-3",
                () => SysMasterAsync(connection, cancellationToken)).ConfigureAwait(false));
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

    /// <summary>
    /// Runs one probe, turning any unexpected exception into a failed result.
    /// </summary>
    /// <remarks>
    /// A spike exists to discover things nobody predicted, so it must survive
    /// discovering them. Letting one probe's surprise abort the run throws away the
    /// answers the rest would have given.
    /// </remarks>
    private static async Task<ProbeResult> SafelyAsync(
        string name,
        string requirement,
        Func<Task<ProbeResult>> probe)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProbeResult.Fail(
                name,
                requirement,
                $"The probe itself threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds out whether an Informix INTERVAL can be read through System.Data.Odbc at all.
    /// </summary>
    /// <remarks>
    /// The first real run died here with "Unknown SQL type - 110" — ODBC's
    /// SQL_INTERVAL_DAY_TO_SECOND, which System.Data.Odbc's type map has no entry
    /// for. It throws before any value conversion, so the usual accessors are
    /// unusable and even IsDBNull is not safe.
    /// <para>
    /// PR-4.5 makes INTERVAL rendering a Must, so this probe establishes which
    /// access path, if any, survives. The answer decides whether the ODBC branch of
    /// DEC-4 can meet PR-4.5 or whether the provider decision has to be reopened.
    /// </para>
    /// </remarks>
    private static async Task<ProbeResult> IntervalAccessAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FIRST 1 INTERVAL (5 12:30:45) DAY TO SECOND AS iv FROM systables
            """;

        using var command = new OdbcCommand(sql, connection) { CommandTimeout = 30 };

        using OdbcDataReader reader = (OdbcDataReader)await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ProbeResult.Inconclusive("Interval access", "PR-4.5", "No row returned.", sql);
        }

        var findings = new List<string>();
        var anyWorked = false;

        // Metadata first: these do not convert a value, so they may survive where
        // the accessors do not.
        findings.Add(Try("GetName", () => reader.GetName(0)));
        findings.Add(Try("GetDataTypeName", () => reader.GetDataTypeName(0)));
        findings.Add(Try("GetFieldType", () => reader.GetFieldType(0)?.Name ?? "null"));
        findings.Add(Try("GetSchemaTable", () => reader.GetSchemaTable() is null ? "null" : "ok"));

        // Then every way of getting at the value.
        findings.Add(Track(Try("IsDBNull", () => reader.IsDBNull(0).ToString())));
        findings.Add(Track(Try("GetValue", () => reader.GetValue(0)?.ToString() ?? "null")));
        findings.Add(Track(Try("GetString", () => reader.GetString(0))));
        findings.Add(Track(Try("GetFieldValue<string>", () => reader.GetFieldValue<string>(0))));
        findings.Add(Track(Try("GetChars", () => ReadChars(reader, 0))));

        // IMS infers nullness on these columns from InvalidCastException, because
        // IsDBNull throws before it can answer. That assumption decides whether a
        // NULL interval renders as "(null)" (PR-4.4) or as something wrong, so it
        // is measured rather than trusted.
        findings.Add(await NullIntervalBehaviourAsync(connection, cancellationToken)
            .ConfigureAwait(false));

        string detail = "How an INTERVAL column can be reached:" + Environment.NewLine
                        + string.Join(Environment.NewLine, findings.Select(f => "      " + f));

        return anyWorked
            ? ProbeResult.Pass("Interval access", "PR-4.5",
                detail + Environment.NewLine
                       + "      At least one value accessor works — PR-4.5 is reachable over ODBC.",
                sql)
            : ProbeResult.Fail("Interval access", "PR-4.5",
                detail + Environment.NewLine
                       + "      NO value accessor works. PR-4.5 cannot be met over System.Data.Odbc "
                       + "without direct ODBC interop, and DEC-4 needs reopening.",
                sql);

        string Track(string finding)
        {
            if (finding.Contains("=> ok:", StringComparison.Ordinal))
            {
                anyWorked = true;
            }

            return finding;
        }
    }

    /// <summary>
    /// What <c>GetString</c> does with a NULL interval, since <c>IsDBNull</c> cannot
    /// be asked.
    /// </summary>
    private static async Task<string> NullIntervalBehaviourAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT FIRST 1 CAST(NULL AS INTERVAL DAY TO SECOND) AS iv FROM systables";

        try
        {
            using var command = new OdbcCommand(sql, connection) { CommandTimeout = 30 };

            using OdbcDataReader reader = (OdbcDataReader)await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return "NULL interval           => no row returned";
            }

            try
            {
                string? value = reader.GetString(0);

                return $"NULL interval           => GetString returned "
                       + (value is null ? "null" : $"'{value}' (NOT an exception)");
            }
            catch (Exception ex)
            {
                return $"NULL interval           => GetString threw {ex.GetType().Name} "
                       + "(IMS treats InvalidCastException as SQL NULL)";
            }
        }
        catch (OdbcException ex)
        {
            return $"NULL interval           => could not test: {Describe(ex)}";
        }
    }

    private static string ReadChars(OdbcDataReader reader, int ordinal)
    {
        var buffer = new char[64];
        long read = reader.GetChars(ordinal, 0, buffer, 0, buffer.Length);

        return new string(buffer, 0, (int)Math.Max(read, 0));
    }

    private static string Try(string label, Func<string?> action)
    {
        try
        {
            return $"{label,-22} => ok: {action() ?? "null"}";
        }
        catch (Exception ex)
        {
            return $"{label,-22} => {ex.GetType().Name}: {ex.Message}";
        }
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

            string summary = string.Join(" | ", detail);

            // The SQLCODE is the part PR-3.6 always needs, and it either arrives or
            // it does not. The ISAM error is a different question: most errors do
            // not have one. -206 (table not found) is purely a SQL-level error, so a
            // single diagnostic record here is correct rather than a shortfall —
            // treating it as a failure was the probe being wrong, not the driver.
            //
            // Whether Informix surfaces an ISAM code through ODBC when there IS one
            // needs an error that produces one, and those are all either
            // write operations or need a second session holding a lock. Left for a
            // run against a non-production instance where that is safe to arrange.
            return sawNativeCode
                ? ProbeResult.Pass(
                    "Error detail",
                    "PR-3.6",
                    $"SQLCODE retrievable. {ex.Errors.Count} diagnostic record(s): {summary}"
                    + Environment.NewLine
                    + "      ISAM reporting is NOT proven by this probe — SQLCODE -206 has no ISAM "
                    + "error. Needs a lock conflict or constraint violation to confirm.",
                    sql)
                : ProbeResult.Fail(
                    "Error detail",
                    "PR-3.6",
                    $"No native error code came through at all, so PR-3.6 cannot be met as "
                    + $"written. {summary}",
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

                // Per column, because a single unreadable one must not cost the
                // report on all the others. System.Data.Odbc throws from inside its
                // type map for Informix INTERVAL, before any value handling.
                try
                {
                    string serverType = reader.GetDataTypeName(i);

                    object? raw = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                        ? null
                        : reader.GetValue(i);

                    InformixDbType mapped = InformixTypeMapper.FromServerTypeName(serverType);

                    lines.Add(
                        $"{name}: server='{serverType}' clr={raw?.GetType().Name ?? "null"} "
                        + $"mapped={mapped} value='{raw}'");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lines.Add($"{name}: UNREADABLE — {ex.GetType().Name}: {ex.Message}");
                }
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
