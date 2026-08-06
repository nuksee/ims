using System.Collections.Concurrent;
using Ims.Core.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ims.Core.Completion;

/// <summary>
/// The object-metadata cache completion reads from (PR-3.2).
/// </summary>
/// <remarks>
/// <para>
/// This is the cache the to-do list said Slice 1b would defer into Slice 2 so it
/// would only be built once. The object tree fetches on expansion because that is
/// what the user asked for; completion cannot, because nobody expands anything
/// before typing a table name. So the names are warmed once per connection, and
/// columns arrive per table on first mention.
/// </para>
/// <para>
/// PR-6.4 caps the cost: one listing per object kind at connect, then at most one
/// column query per table named in an editor, ever. Nothing polls, and nothing
/// refetches on a keystroke — <see cref="Invalidate"/> is the only way back to the
/// server, and only the user triggers it.
/// </para>
/// <para>
/// Every accessor is synchronous and lock-free by construction: the collections are
/// concurrent and the object list is swapped wholesale rather than mutated. A reader
/// on the UI thread never waits for a writer on a background one.
/// </para>
/// </remarks>
public sealed class CatalogCache : ICatalogSnapshot
{
    private static readonly SchemaObjectKind[] NamedKinds =
    [
        SchemaObjectKind.Table,
        SchemaObjectKind.View,
        SchemaObjectKind.Synonym,
        SchemaObjectKind.Sequence,
        SchemaObjectKind.Procedure,
        SchemaObjectKind.Function,
    ];

    private readonly ICatalogReader _reader;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _columns =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _columnsRequested =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile IReadOnlyList<SchemaObject> _objects = [];
    private volatile IReadOnlyList<string> _owners = [];

    public CatalogCache(ICatalogReader reader, ILogger<CatalogCache>? logger = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? NullLogger<CatalogCache>.Instance;
    }

    public IReadOnlyList<SchemaObject> Objects => _objects;

    public IReadOnlyList<string> Owners => _owners;

    /// <summary>Raised when a background fetch has added something. Never on the UI thread.</summary>
    public event EventHandler? Updated;

    public IReadOnlyList<string> ColumnsOf(string name, string? owner) =>
        _columns.TryGetValue(Key(name, owner), out IReadOnlyList<string>? columns)
            ? columns

            // Owner-blind second try. A statement that says "customer" without an owner
            // still means a real table, and the cache keyed it by the owner the
            // catalogue reported.
            : _columns.TryGetValue(Key(name, null), out IReadOnlyList<string>? unqualified)
                ? unqualified
                : [];

    public void RequestColumns(string name, string? owner)
    {
        string key = Key(name, owner);

        // Once per table, whatever the keystroke rate. Also stops a table the user has
        // no permission on from being retried on every character they type.
        if (!_columnsRequested.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(() => LoadColumnsAsync(name, owner, key, CancellationToken.None));
    }

    /// <summary>
    /// Loads the object names, once, at connect.
    /// </summary>
    /// <remarks>
    /// Failure is not propagated. An editor whose completion knows only the language
    /// is still a working editor, and PR-8.4 prefers a capability quietly absent to an
    /// application that will not start — but it is logged, so it is not invisible.
    /// </remarks>
    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        var objects = new List<SchemaObject>();

        foreach (SchemaObjectKind kind in NamedKinds)
        {
            try
            {
                CatalogResult<SchemaObject> result = await _reader
                    .GetObjectsAsync(kind, null, null, includeSystem: false, cancellationToken)
                    .ConfigureAwait(false);

                objects.AddRange(result.Items);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(
                    "Completion will not offer {Kind} names: {Message}", kind, ex.Message);
            }
        }

        _objects = objects;

        try
        {
            CatalogResult<string> owners = await _reader
                .GetOwnersAsync(cancellationToken).ConfigureAwait(false);

            _owners = owners.Items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Completion will not offer owner names: {Message}", ex.Message);
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops everything, so the next warm goes back to the server (PR-2.7).</summary>
    public void Invalidate()
    {
        _objects = [];
        _owners = [];
        _columns.Clear();
        _columnsRequested.Clear();
    }

    private async Task LoadColumnsAsync(
        string name,
        string? owner,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            SchemaObject? match = _objects.FirstOrDefault(o =>
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
                && (owner is null || string.Equals(o.Owner, owner, StringComparison.OrdinalIgnoreCase)));

            if (match is null)
            {
                // Not a table IMS has heard of — a typo, or something the listing did
                // not return. Recording the empty result stops it being asked for again.
                _columns[key] = [];
                return;
            }

            TableDetail detail = await _reader
                .GetTableDetailAsync(match.TabId, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> columns = detail.Columns.Select(c => c.Name).ToList();

            _columns[key] = columns;
            _columns[Key(match.Name, match.Owner)] = columns;
            _columns[Key(match.Name, null)] = columns;

            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _columns[key] = [];

            _logger.LogInformation(
                "Completion will not offer columns for {Table}: {Message}", name, ex.Message);
        }
    }

    private static string Key(string name, string? owner) =>
        string.IsNullOrWhiteSpace(owner) ? name : owner + "." + name;
}
