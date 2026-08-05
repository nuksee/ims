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
    /// Opt-in for the probes that put real load on the server.
    /// </summary>
    /// <remarks>
    /// RSK-5 and PR-6.4. The cancellation probe needs a statement slow enough to
    /// cancel, which means a deliberately expensive query. That is fine on the
    /// non-production instance DEP-2 asks for and not fine anywhere else, so it
    /// does not run unless asked for.
    /// </remarks>
    public bool IncludeLoadProbes { get; set; }

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
