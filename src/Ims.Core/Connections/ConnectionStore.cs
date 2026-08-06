using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ims.Core.Connections;

/// <summary>
/// The saved instance list (PR-1.2).
/// </summary>
/// <remarks>
/// <para>
/// A flat, searchable, groupable list — DEC-7 designs for under ten instances, so
/// there is no folder hierarchy, no tagging and no inventory sync to build.
/// </para>
/// <para>
/// This file holds no secrets and cannot: <see cref="ConnectionDescriptor"/> has no
/// password field, so PR-1.4 holds by construction rather than by care. Passwords
/// live in Windows Credential Manager, keyed by the descriptor's id (DEC-9).
/// </para>
/// </remarks>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<ConnectionDescriptor> _connections = [];

    public ConnectionStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The default location: <c>%APPDATA%\IMS\connections.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IMS",
        "connections.json");

    public IReadOnlyList<ConnectionDescriptor> Connections => _connections;

    /// <summary>Reads the list, or starts an empty one if there is no file yet.</summary>
    /// <remarks>
    /// A corrupt file is reported rather than silently discarded — losing someone's
    /// instance list without saying so would be worse than failing to start.
    /// </remarks>
    public void Load()
    {
        _connections.Clear();

        if (!File.Exists(_path))
        {
            return;
        }

        string json = File.ReadAllText(_path);

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        List<ConnectionDescriptor>? loaded;

        try
        {
            loaded = JsonSerializer.Deserialize<List<ConnectionDescriptor>>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The saved connection list at '{_path}' could not be read. "
                + "It has been left untouched so it can be recovered or repaired by hand.",
                ex);
        }

        if (loaded is not null)
        {
            _connections.AddRange(loaded);
        }
    }

    /// <summary>Writes the list, creating the directory if needed.</summary>
    public void Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temporary file and move it into place, so an interrupted save
        // cannot leave a half-written list where the real one was.
        string temporary = _path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(_connections, SerializerOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>Adds a connection, or replaces the one with the same id.</summary>
    public void AddOrUpdate(ConnectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        int existing = _connections.FindIndex(c => c.Id == descriptor.Id);

        if (existing >= 0)
        {
            _connections[existing] = descriptor;
        }
        else
        {
            _connections.Add(descriptor);
        }
    }

    /// <summary>Removes a connection. Returns false when there was nothing to remove.</summary>
    public bool Remove(Guid id) => _connections.RemoveAll(c => c.Id == id) > 0;

    public ConnectionDescriptor? Find(Guid id) => _connections.Find(c => c.Id == id);

    /// <summary>
    /// Searches by name, server, host or group — PR-1.2's "searchable by name",
    /// widened slightly because searching for the host you know is the same act.
    /// </summary>
    public IReadOnlyList<ConnectionDescriptor> Search(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return _connections;
        }

        return _connections
            .Where(c =>
                Contains(c.DisplayName, term)
                || Contains(c.ServerName, term)
                || Contains(c.Host, term)
                || Contains(c.Group, term)
                || Contains(c.Database, term))
            .ToList();
    }

    /// <summary>
    /// Groups for display (PR-1.2). Production sorts first, because PR-1.5 is about
    /// a production connection never being mistaken for a non-production one.
    /// </summary>
    public IReadOnlyList<IGrouping<string, ConnectionDescriptor>> Grouped(
        IEnumerable<ConnectionDescriptor>? subset = null) =>
        (subset ?? _connections)
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Group)
                ? DescribeEnvironment(c.Environment)
                : c.Group)
            .OrderBy(g => GroupRank(g.Key), StringComparer.Ordinal)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string DescribeEnvironment(InformixEnvironment environment) => environment switch
    {
        InformixEnvironment.Production => "Production",
        InformixEnvironment.Uat => "UAT",
        InformixEnvironment.Development => "Development",
        _ => "Ungrouped",
    };

    private static string GroupRank(string group) => group switch
    {
        "Production" => "0",
        "UAT" => "1",
        "Development" => "2",
        _ => "3",
    };

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null
        && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
