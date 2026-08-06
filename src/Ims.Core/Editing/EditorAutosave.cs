using System.Text.Json;

namespace Ims.Core.Editing;

/// <summary>A recovered editor tab.</summary>
public sealed record AutosavedTab
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Sql { get; init; }

    public string? FilePath { get; init; }

    public required DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// Keeps unsaved editor content across an unexpected termination (PR-3.9).
/// </summary>
/// <remarks>
/// <para>
/// NFR-3 accepts that a crash may happen and requires that unsaved content survive
/// it. Slice 1's acceptance criteria put it plainly: "Unsaved editor content
/// survives killing the process."
/// </para>
/// <para>
/// One file per tab, written whole and moved into place, so a crash during a save
/// costs the last few seconds of one tab rather than corrupting the lot. Content is
/// stored verbatim — this is the user's own work on their own machine, the same
/// reasoning that governs query history under DEC-8.
/// </para>
/// <para>
/// Lives in Ims.Core rather than the WPF project because it needs nothing from
/// WPF, and because PR-3.9 deserves tests.
/// </para>
/// </remarks>
public sealed class EditorAutosave
{
    private readonly string _directory;
    private readonly Lock _writeLock = new();

    public EditorAutosave(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;
    }

    /// <summary>The default location: <c>%LOCALAPPDATA%\IMS\autosave</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IMS",
        "autosave");

    /// <summary>Writes a tab's current content. Never throws.</summary>
    public void Save(string id, string title, string sql, string? filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var tab = new AutosavedTab
        {
            Id = id,
            Title = title,
            Sql = sql,
            FilePath = filePath,
            SavedAt = DateTimeOffset.Now,
        };

        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                string path = PathFor(id);
                string temporary = path + ".tmp";

                File.WriteAllText(temporary, JsonSerializer.Serialize(tab));
                File.Move(temporary, path, overwrite: true);
            }
            catch (IOException)
            {
                // Autosave failing must never interrupt typing.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Forgets a tab, once it is saved to a file or deliberately closed.</summary>
    public void Discard(string id)
    {
        lock (_writeLock)
        {
            try
            {
                string path = PathFor(id);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Everything left behind by a previous run, newest first.</summary>
    public IReadOnlyList<AutosavedTab> Recover()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var recovered = new List<AutosavedTab>();

        try
        {
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AutosavedTab>(File.ReadAllText(path))
                        is { } tab && !string.IsNullOrWhiteSpace(tab.Sql))
                    {
                        recovered.Add(tab);
                    }
                }
                catch (JsonException)
                {
                    // One unreadable file must not cost the others.
                }
                catch (IOException)
                {
                }
            }
        }
        catch (IOException)
        {
            return recovered;
        }

        return recovered.OrderByDescending(t => t.SavedAt).ToList();
    }

    /// <summary>Clears everything, once the user has dealt with a recovery.</summary>
    public void DiscardAll()
    {
        foreach (AutosavedTab tab in Recover())
        {
            Discard(tab.Id);
        }
    }

    private string PathFor(string id) => Path.Combine(_directory, $"{SanitiseId(id)}.json");

    /// <summary>
    /// Makes a tab title safe to use as a file name.
    /// </summary>
    /// <remarks>
    /// A tab is often named after a file, and a title such as <c>..\..\evil</c>
    /// must not escape the autosave directory.
    /// </remarks>
    private static string SanitiseId(string id)
    {
        Span<char> buffer = stackalloc char[id.Length];

        for (int i = 0; i < id.Length; i++)
        {
            buffer[i] = char.IsLetterOrDigit(id[i]) || id[i] is '-' or '_' ? id[i] : '_';
        }

        return new string(buffer);
    }
}
