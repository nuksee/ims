using Ims.Core.Connections;

namespace Ims.SmokeTest;

/// <summary>Command-line options for the spike.</summary>
public sealed class SmokeTestOptions
{
    public string? ServerName { get; set; }

    public string? Host { get; set; }

    public string? Service { get; set; }

    public string Protocol { get; set; } = "onsoctcp";

    public string? Database { get; set; }

    public string? UserName { get; set; }

    /// <summary>
    /// Supplied non-interactively. Discouraged: a password on the command line is
    /// visible in the process list, so the default is to prompt.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Opt-in for bounded versions of the streaming and cancellation probes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DEP-2 asks for an instance of its own, and the estate does not have one: the
    /// test database shares a server with production. So the choice is not "load
    /// probes or no answers" but "bounded load or no answers", and these are the
    /// bounded half.
    /// </para>
    /// <para>
    /// Every statement is capped <em>server-side</em> with <c>FIRST n</c> and carries
    /// a <c>CommandTimeout</c>, so the work is known before it is sent rather than
    /// stopped once it is running. That is the difference that matters when a
    /// runaway would land on the same instance as production (RSK-5, PR-6.4).
    /// </para>
    /// </remarks>
    public bool IncludeLightLoadProbes { get; set; }

    /// <summary>
    /// Opt-in for the unbounded probes.
    /// </summary>
    /// <remarks>
    /// RSK-5 and PR-6.4. The cancellation probe needs a statement slow enough to
    /// cancel, and the unbounded form gets there with a four-way cross join and no
    /// timeout — if the cancel does not land, nothing stops it. That is fine on a
    /// server of its own and not fine on one shared with production, so it stays
    /// behind its own flag and <see cref="IncludeLightLoadProbes"/> is the option to
    /// reach for first.
    /// </remarks>
    public bool IncludeLoadProbes { get; set; }

    /// <summary>
    /// True when either load tier was asked for. The bounded tier is implied by the
    /// unbounded one, so <c>--include-load</c> alone still runs everything.
    /// </summary>
    public bool AnyLoadProbes => IncludeLightLoadProbes || IncludeLoadProbes;

    /// <summary>
    /// Re-runs the synchronous cancellation probes, which are otherwise skipped.
    /// </summary>
    /// <remarks>
    /// They answered their question on 2026-08-06 — <c>Cancel()</c> does not reach the
    /// server, on a sorting or a scanning workload alike — and each costs a 30-second
    /// cross join to say so again. On an instance shared with production that is a
    /// recurring cost for a known answer, so they are off by default.
    /// <para>
    /// Worth turning on to re-measure after a driver or server upgrade, or to
    /// re-establish the synchronous baseline the async spike is compared against.
    /// </para>
    /// </remarks>
    public bool RecheckCancellation { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public bool ShowHelp { get; set; }

    /// <summary>Errors found while parsing. Non-empty means do not run.</summary>
    public List<string> Errors { get; } = [];

    public ConnectionDescriptor ToDescriptor() => new()
    {
        Id = Guid.Empty,
        DisplayName = ServerName ?? "smoke-test",
        ServerName = ServerName!,
        Host = Host!,
        Service = Service!,
        Protocol = Protocol,
        Database = Database,
        UserName = UserName,

        // The spike is for non-production instances only (DEP-2, RSK-5), and saying
        // so here keeps it out of any code path that treats production differently.
        Environment = InformixEnvironment.Development,
        ConnectTimeoutSeconds = TimeoutSeconds,
    };

    public static SmokeTestOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new SmokeTestOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    options.ShowHelp = true;
                    return options;

                case "--include-light-load":
                    options.IncludeLightLoadProbes = true;
                    continue;

                case "--recheck-cancellation":
                    options.RecheckCancellation = true;
                    continue;

                case "--include-load":
                    options.IncludeLoadProbes = true;
                    continue;
            }

            if (i + 1 >= args.Length)
            {
                options.Errors.Add($"'{arg}' expects a value.");
                break;
            }

            string value = args[++i];

            switch (arg)
            {
                case "--server": options.ServerName = value; break;
                case "--host": options.Host = value; break;
                case "--service": options.Service = value; break;
                case "--protocol": options.Protocol = value; break;
                case "--database": options.Database = value; break;
                case "--user": options.UserName = value; break;
                case "--password": options.Password = value; break;
                case "--timeout":
                    if (int.TryParse(value, out int timeout) && timeout > 0)
                    {
                        options.TimeoutSeconds = timeout;
                    }
                    else
                    {
                        options.Errors.Add($"--timeout expects a positive number, got '{value}'.");
                    }

                    break;
                default:
                    options.Errors.Add($"Unknown option '{arg}'.");
                    break;
            }
        }

        if (!options.ShowHelp)
        {
            if (string.IsNullOrWhiteSpace(options.ServerName))
            {
                options.Errors.Add("--server is required.");
            }

            if (string.IsNullOrWhiteSpace(options.Host))
            {
                options.Errors.Add("--host is required.");
            }

            if (string.IsNullOrWhiteSpace(options.Service))
            {
                options.Errors.Add("--service is required.");
            }
        }

        return options;
    }
}
