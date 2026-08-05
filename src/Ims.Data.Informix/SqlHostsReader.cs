using Microsoft.Win32;

namespace Ims.Data.Informix;

/// <summary>One connectivity entry: a server name mapped to a network endpoint.</summary>
public sealed record SqlHostsEntry
{
    public required string ServerName { get; init; }

    /// <summary>Network protocol, e.g. <c>onsoctcp</c>.</summary>
    public required string Protocol { get; init; }

    public required string Host { get; init; }

    /// <summary>Service name or port number.</summary>
    public required string Service { get; init; }

    /// <summary>Trailing options field, unparsed. Shown verbatim (PR-8.2).</summary>
    public string? Options { get; init; }

    /// <summary>Where this entry was read from, so the UI can say which source won.</summary>
    public required SqlHostsSource Source { get; init; }
}

/// <summary>Where a connectivity entry came from.</summary>
public enum SqlHostsSource
{
    /// <summary>The <c>sqlhosts</c> text file under <c>%INFORMIXDIR%\etc</c>.</summary>
    File,

    /// <summary>The <c>HKLM\SOFTWARE\Informix\SqlHosts</c> registry key.</summary>
    Registry,
}

/// <summary>
/// Reads Informix connectivity configuration from the two places Windows keeps it.
/// </summary>
/// <remarks>
/// <para>
/// Groundwork for PR-1.9 ("import an existing <c>sqlhosts</c> file to populate the
/// instance list") and for PR-1.1, whose server/host/service/protocol quartet is
/// deliberately the same shape as an entry here.
/// </para>
/// <para>
/// Both sources are read because Windows genuinely uses both: the CSDK installer
/// writes the registry key, and the text file is what gets copied between machines.
/// On the development workstation the same server appears in both.
/// </para>
/// <para>
/// Parsing is separated from I/O so the whole of it is testable with no machine
/// state and no server.
/// </para>
/// </remarks>
public static class SqlHostsReader
{
    private const string SqlHostsRegistryKey = @"SOFTWARE\Informix\SqlHosts";

    /// <summary>
    /// Reads every entry this machine knows about, registry first.
    /// </summary>
    /// <remarks>
    /// Entries are not de-duplicated. Which source to prefer is a decision for the
    /// import UI, and hiding a discrepancy between the two would be the wrong
    /// default when the discrepancy is exactly what the user needs to see.
    /// </remarks>
    public static IReadOnlyList<SqlHostsEntry> ReadAll()
    {
        var entries = new List<SqlHostsEntry>();

        entries.AddRange(ReadFromRegistry());

        string? informixDir = CsdkLocator.ReadInformixDir();
        if (!string.IsNullOrWhiteSpace(informixDir))
        {
            string path = Path.Combine(informixDir.TrimEnd('\\', '/'), "etc", "sqlhosts");
            entries.AddRange(ReadFromFile(path));
        }

        return entries;
    }

    /// <summary>Reads the <c>HKLM\SOFTWARE\Informix\SqlHosts</c> subkeys.</summary>
    public static IReadOnlyList<SqlHostsEntry> ReadFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(SqlHostsRegistryKey);
        if (root is null)
        {
            return [];
        }

        var entries = new List<SqlHostsEntry>();

        foreach (string serverName in root.GetSubKeyNames())
        {
            using RegistryKey? key = root.OpenSubKey(serverName);
            if (key is null)
            {
                continue;
            }

            string? host = key.GetValue("HOST") as string;
            string? service = key.GetValue("SERVICE") as string;
            string? protocol = key.GetValue("PROTOCOL") as string;

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            entries.Add(new SqlHostsEntry
            {
                ServerName = serverName,
                Protocol = string.IsNullOrWhiteSpace(protocol) ? "onsoctcp" : protocol,
                Host = host,
                Service = service,
                Options = key.GetValue("OPTIONS") as string,
                Source = SqlHostsSource.Registry,
            });
        }

        return entries;
    }

    /// <summary>Reads a <c>sqlhosts</c> file, returning nothing if it is absent.</summary>
    public static IReadOnlyList<SqlHostsEntry> ReadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Parse(File.ReadAllLines(path));
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses <c>sqlhosts</c> content: whitespace-separated
    /// <c>servername protocol host service [options]</c>, with <c>#</c> comments.
    /// </summary>
    public static IReadOnlyList<SqlHostsEntry> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<SqlHostsEntry>();

        foreach (string rawLine in lines)
        {
            string line = rawLine;

            int comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Anything short of the four mandatory fields is not an entry.
            if (fields.Length < 4)
            {
                continue;
            }

            entries.Add(new SqlHostsEntry
            {
                ServerName = fields[0],
                Protocol = fields[1],
                Host = fields[2],
                Service = fields[3],
                Options = fields.Length > 4 ? string.Join(' ', fields[4..]) : null,
                Source = SqlHostsSource.File,
            });
        }

        return entries;
    }
}
