using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ims.Core.History;

/// <summary>One executed statement, as the user will see it in history.</summary>
public sealed record QueryHistoryEntry
{
    public required DateTimeOffset ExecutedAt { get; init; }

    /// <summary>The statement as typed. Not redacted — see the remarks on the store.</summary>
    public required string Sql { get; init; }

    /// <summary>Which instance it ran against (PR-3.12).</summary>
    public required string Target { get; init; }

    public string? Database { get; init; }

    public required double ElapsedMilliseconds { get; init; }

    /// <summary>Rows returned or affected, where either applies.</summary>
    public long? RowCount { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>The error summary when it failed, so history explains itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A local, searchable record of what has been run (PR-3.12).
/// </summary>
/// <remarks>
/// <para>
/// DEC-8 settles what this is and is not: "No tamper-proof audit trail. Local,
/// user-visible query history only." It follows from DEC-2 — IMS takes no
/// privileged action of its own, so there is nothing IMS-specific to audit, and
/// Informix's own logging remains the record.
/// </para>
/// <para>
/// Statements are stored verbatim, unlike in the application log. That is
/// deliberate and consistent with PR-6.3, which is about what IMS writes into
/// <em>logs</em>. History is the user's own record of their own work — a history
/// with the literals stripped out would not answer "what did I run yesterday",
/// which is the entire point of PR-3.12.
/// </para>
/// <para>
/// Append-only JSON Lines, so a crash mid-write costs at most the last entry, and
/// so the file can be read with any text tool the user already has.
/// </para>
/// </remarks>
public sealed class QueryHistory
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly int _maximumEntries;
    private readonly Lock _writeLock = new();

    public QueryHistory(string path, int maximumEntries = 5000)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _maximumEntries = maximumEntries;
    }

    /// <summary>The default location: <c>%APPDATA%\IMS\history.jsonl</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IMS",
        "history.jsonl");

    /// <summary>Appends an entry. Never throws — losing history must not lose work.</summary>
    public void Add(QueryHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_writeLock)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllLines(_path, [JsonSerializer.Serialize(entry, SerializerOptions)]);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Reads history, newest first. A corrupt line is skipped rather than failing
    /// the whole read — one bad entry must not cost the user the rest.
    /// </summary>
    public IReadOnlyList<QueryHistoryEntry> Read(int limit = 500)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var entries = new List<QueryHistoryEntry>();

        try
        {
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<QueryHistoryEntry>(line, SerializerOptions)
                        is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
            return entries;
        }

        entries.Reverse();

        return limit > 0 && entries.Count > limit ? entries[..limit] : entries;
    }

    /// <summary>
    /// Searches the statement text, target and database — PR-3.12's "searchable".
    /// </summary>
    public IReadOnlyList<QueryHistoryEntry> Search(string? term, int limit = 500)
    {
        IReadOnlyList<QueryHistoryEntry> all = Read(limit: 0);

        IEnumerable<QueryHistoryEntry> matches = string.IsNullOrWhiteSpace(term)
            ? all
            : all.Where(e =>
                Contains(e.Sql, term)
                || Contains(e.Target, term)
                || Contains(e.Database, term));

        return limit > 0 ? matches.Take(limit).ToList() : matches.ToList();
    }

    /// <summary>
    /// Trims the file to <see cref="_maximumEntries"/>, keeping the newest.
    /// </summary>
    /// <remarks>
    /// Called at shutdown rather than on every write, because rewriting the file on
    /// each statement would make PR-8.5 worse for no benefit.
    /// </remarks>
    public void Trim()
    {
        lock (_writeLock)
        {
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(_path);

                if (lines.Length <= _maximumEntries)
                {
                    return;
                }

                string temporary = _path + ".tmp";
                File.WriteAllLines(temporary, lines[^_maximumEntries..]);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null
        && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
