using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ims.Core.Catalog;

namespace Ims.App.ViewModels;

/// <summary>
/// A node in the object tree.
/// </summary>
/// <remarks>
/// <para>
/// PR-2.2 requires children to load strictly on demand, so expanding a large
/// database never stalls the UI, and NFR-2 sets that at 20,000+ objects. The
/// mechanism is the usual one: an unloaded node carries a single placeholder child
/// so the expander appears, and the real children replace it on first expansion.
/// </para>
/// <para>
/// Loading happens off the dispatcher — <c>ServerCallGuard</c> throws otherwise —
/// and a node that fails to load says so in its own label rather than throwing into
/// the UI. Expanding a node the user has no permission on is a normal event, not an
/// exceptional one.
/// </para>
/// </remarks>
public abstract partial class CatalogNodeViewModel : ObservableObject
{
    private static readonly CatalogNodeViewModel[] Placeholder =
        [new PlaceholderNodeViewModel()];

    private bool _isLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    protected CatalogNodeViewModel(string name, bool canHaveChildren)
    {
        Name = name;

        if (canHaveChildren)
        {
            foreach (CatalogNodeViewModel placeholder in Placeholder)
            {
                Children.Add(placeholder);
            }
        }
    }

    public string Name { get; protected set; }

    public ObservableCollection<CatalogNodeViewModel> Children { get; } = [];

    /// <summary>A short glyph for the row. Text, so it needs no icon assets.</summary>
    public virtual string Glyph => "•";

    /// <summary>Secondary text, shown greyed after the name.</summary>
    public virtual string? Detail => null;

    /// <summary>The catalogue query behind this node's children, for PR-8.2.</summary>
    public string? SourceQuery { get; protected set; }

    /// <summary>Loads children the first time the node is expanded.</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_isLoaded || IsLoading)
        {
            return;
        }

        IsLoading = true;
        Error = null;

        try
        {
            IReadOnlyList<CatalogNodeViewModel> children =
                await LoadChildrenAsync(cancellationToken).ConfigureAwait(true);

            Children.Clear();

            foreach (CatalogNodeViewModel child in children)
            {
                Children.Add(child);
            }

            _isLoaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NFR-4: degrade with an explanation rather than failing opaquely. A
            // restricted catalogue table is an ordinary thing to meet.
            Children.Clear();
            Children.Add(new PlaceholderNodeViewModel($"Could not load: {ex.Message}"));
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Discards loaded children so the next expansion refetches (PR-2.7).</summary>
    public void Invalidate()
    {
        _isLoaded = false;
        Children.Clear();

        foreach (CatalogNodeViewModel placeholder in Placeholder)
        {
            Children.Add(placeholder);
        }
    }

    protected abstract Task<IReadOnlyList<CatalogNodeViewModel>> LoadChildrenAsync(
        CancellationToken cancellationToken);
}

/// <summary>The "expand me" stand-in, and the place load failures are reported.</summary>
public sealed class PlaceholderNodeViewModel(string text = "Loading…")
    : CatalogNodeViewModel(text, canHaveChildren: false)
{
    public override string Glyph => " ";

    protected override Task<IReadOnlyList<CatalogNodeViewModel>> LoadChildrenAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogNodeViewModel>>([]);
}

/// <summary>A grouping node — Tables, Views, Procedures and so on (PR-2.1).</summary>
public sealed class ObjectFolderNodeViewModel : CatalogNodeViewModel
{
    private readonly ICatalogReader _catalog;
    private readonly SchemaObjectKind _kind;
    private readonly Func<string?> _nameFilter;
    private readonly Func<bool> _includeSystem;

    public ObjectFolderNodeViewModel(
        string name,
        SchemaObjectKind kind,
        ICatalogReader catalog,
        Func<string?> nameFilter,
        Func<bool> includeSystem)
        : base(name, canHaveChildren: true)
    {
        _kind = kind;
        _catalog = catalog;
        _nameFilter = nameFilter;
        _includeSystem = includeSystem;
    }

    public override string Glyph => "📁";

    protected override async Task<IReadOnlyList<CatalogNodeViewModel>> LoadChildrenAsync(
        CancellationToken cancellationToken)
    {
        string? filter = _nameFilter();
        bool includeSystem = _includeSystem();

        // Off the dispatcher: this is a server round trip (NFR-1).
        CatalogResult<SchemaObject> result = await Task.Run(
            () => _catalog.GetObjectsAsync(_kind, filter, null, includeSystem, cancellationToken),
            cancellationToken).ConfigureAwait(true);

        SourceQuery = result.Sql;

        if (result.Items.Count == 0)
        {
            return [new PlaceholderNodeViewModel(
                filter is null ? "(none)" : "(none matching)")];
        }

        return result.Items
            .Select(o => (CatalogNodeViewModel)new SchemaObjectNodeViewModel(o, result.Sql))
            .ToList();
    }
}

/// <summary>One object: a table, view, procedure, index and so on.</summary>
public sealed class SchemaObjectNodeViewModel : CatalogNodeViewModel
{
    public SchemaObjectNodeViewModel(SchemaObject schemaObject, string sourceQuery)
        : base(schemaObject.Name, canHaveChildren: false)
    {
        Object = schemaObject;
        SourceQuery = sourceQuery;
    }

    public SchemaObject Object { get; }

    public override string Glyph => Object.Kind switch
    {
        SchemaObjectKind.Table => "▦",
        SchemaObjectKind.View => "◫",
        SchemaObjectKind.Synonym or SchemaObjectKind.PrivateSynonym => "↪",
        SchemaObjectKind.Sequence => "#",
        SchemaObjectKind.Procedure => "⚙",
        SchemaObjectKind.Function => "ƒ",
        SchemaObjectKind.Index => "⑂",
        SchemaObjectKind.UserDefinedType => "T",
        _ => "•",
    };

    public override string? Detail
    {
        get
        {
            // The row count is only as fresh as the statistics, and PR-2.5 is about
            // not letting people forget that. The detail pane says so properly; here
            // the tilde is the reminder that it is an estimate.
            string owner = Object.Owner;

            return Object.EstimatedRows is { } rows
                ? $"{owner} — ~{rows:N0} rows"
                : owner;
        }
    }

    protected override Task<IReadOnlyList<CatalogNodeViewModel>> LoadChildrenAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogNodeViewModel>>([]);
}
