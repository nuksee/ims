using System.Globalization;
using System.Text;
using Ims.Data.Informix;

namespace Ims.SmokeTest;

/// <summary>
/// The Slice 0 provider spike.
/// </summary>
/// <remarks>
/// Answers, against a real non-production instance, the questions that cannot be
/// answered without one. Its output is what turns the "delivered but unverified"
/// items of Slice 0 into verified ones.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        SmokeTestOptions options = SmokeTestOptions.Parse(args);

        if (options.ShowHelp || args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        if (options.Errors.Count > 0)
        {
            foreach (string error in options.Errors)
            {
                Console.Error.WriteLine("error: " + error);
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        // PR-1.8: the SDK is checked before anything is attempted, so a missing SDK
        // is reported as a missing SDK rather than as a connection failure.
        CsdkDetectionResult csdk = CsdkLocator.Detect();

        Console.WriteLine();
        Console.WriteLine("Informix Client SDK");
        Console.WriteLine($"  INFORMIXDIR : {csdk.InformixDir ?? "(not found)"}");
        Console.WriteLine($"  Version     : {csdk.Version ?? "(unknown)"}");
        Console.WriteLine($"  ODBC driver : {csdk.OdbcDriverName ?? "(none registered)"}");

        if (!csdk.IsUsable)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"error: {csdk.Message}");
            Console.Error.WriteLine($"       {csdk.Remedy}");
            return 3;
        }

        if (options.Password is null && options.UserName is not null)
        {
            options.Password = PromptForPassword(options.UserName);
        }

        Console.WriteLine();
        Console.WriteLine($"Target: {options.ServerName} at {options.Host}:{options.Service} "
                          + $"({options.Protocol}), database {options.Database ?? "(none)"}");
        Console.WriteLine("This must be a NON-PRODUCTION instance (DEP-2, RSK-5).");

        if (!options.IncludeLoadProbes)
        {
            Console.WriteLine("Load probes (streaming, cancellation) are off. Add --include-load to run them.");
        }

        Console.WriteLine();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        IReadOnlyList<ProbeResult> results;

        try
        {
            results = await Probes.RunAllAsync(options, csdk, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 4;
        }
        catch (Exception ex)
        {
            // Individual probes are already isolated; this is the backstop, so a
            // surprise still produces a report rather than a stack trace.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"The probe run itself failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 5;
        }

        PrintResults(results);

        return results.Any(r => r.Outcome == ProbeOutcome.Fail) ? 1 : 0;
    }

    private static void PrintResults(IReadOnlyList<ProbeResult> results)
    {
        Console.WriteLine("Results");
        Console.WriteLine(new string('-', 78));

        foreach (ProbeResult result in results)
        {
            string marker = result.Outcome switch
            {
                ProbeOutcome.Pass => "PASS",
                ProbeOutcome.Fail => "FAIL",
                ProbeOutcome.Skipped => "SKIP",
                _ => "????",
            };

            Console.WriteLine($"  [{marker}] {result.Name}  ({result.Requirement})");

            if (result.Statement is not null)
            {
                // PR-6.2 and PR-8.2: every statement this tool sent, shown in full.
                foreach (string line in result.Statement.Split('\n'))
                {
                    Console.WriteLine("      > " + line.TrimEnd());
                }
            }

            Console.WriteLine("      " + result.Detail);
            Console.WriteLine();
        }

        int passed = results.Count(r => r.Outcome == ProbeOutcome.Pass);
        int failed = results.Count(r => r.Outcome == ProbeOutcome.Fail);
        int skipped = results.Count(r => r.Outcome == ProbeOutcome.Skipped);
        int inconclusive = results.Count(r => r.Outcome == ProbeOutcome.Inconclusive);

        Console.WriteLine(new string('-', 78));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "  {0} passed, {1} failed, {2} skipped, {3} need a human to read them.",
            passed,
            failed,
            skipped,
            inconclusive));
        Console.WriteLine();
    }

    private static string PromptForPassword(string userName)
    {
        Console.Write($"Password for {userName}: ");

        var builder = new StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.ToString();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""

            Ims.SmokeTest — the Slice 0 provider spike.

            Answers, against a real non-production Informix instance, the questions
            that Slice 0 cannot settle on a developer workstation alone. Run it once
            against 14.10 (DEP-2). 12.10 was descoped on 2026-08-06 (DEC-5), so a
            12.10 run is no longer required — but this tool still answers RSK-9 if
            an instance ever becomes available.

            USAGE
              Ims.SmokeTest --server <name> --host <host> --service <port>
                            [--database <db>] [--user <user>] [--protocol <proto>]
                            [--timeout <seconds>]
                            [--include-light-load | --include-load]

            OPTIONS
              --server         Informix server name, as in sqlhosts.       (required)
              --host           Hostname or IP address.                      (required)
              --service        Service name or port number.                 (required)
              --protocol       Network protocol. Default: onsoctcp.
              --database       Database to open on connect.
              --user           Username. You will be prompted for the password.
              --password       Password, non-interactively. Discouraged — it is
                               visible in the process list. Prefer the prompt.
              --timeout        Connect timeout in seconds. Default: 15.

              --include-light-load
                               Run streaming and cancellation in BOUNDED form. Every
                               statement is capped server-side with FIRST and carries
                               a 30s CommandTimeout, so the work is known before it is
                               sent. Safe enough for a test database that shares a
                               server with production. Start here.

              --include-load   Run them UNBOUNDED: a four-way cross join with no
                               timeout. Nothing stops it if Cancel() does not land, so
                               use it only on an instance of its own (RSK-5, PR-6.4).
                               Implies the bounded tier.

              -h, --help       Show this text and exit. Touches nothing.

            PROBES
              Connect             PR-1.1, DEC-4   ODBC over the CSDK driver works.
              Version banner      RSK-9, NFR-4    Which version this instance is.
              Error detail        PR-3.6          Are SQLCODE and the ISAM error both
                                                  retrievable through ODBC?
              Type fidelity       PR-4.5          What CLR type each Informix type
                                                  arrives as: DATETIME with its
                                                  qualifier, INTERVAL, MONEY, DECIMAL.
              Streaming           PR-4.2, RSK-6   Does the driver stream, or buffer
                                                  the whole result set?
                                                                    [either load flag]
              Cancellation        PR-3.5          Can a running statement be cancelled
                                                  without losing the session?
                                                                    [either load flag]
              sysmaster readable  Q-1, AS-3       THE Slice 3 gate. Run this as an
                                                  ordinary developer, not as informix.

            NOTES
              Every statement sent is printed with its result (PR-6.2, PR-8.2).
              Nothing here writes to the database.
              Point this at a non-production instance only (DEP-2, RSK-5).

            """);
    }
}
