using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ims.Core.Catalog;
using Ims.Core.Connections;

namespace Ims.App.ViewModels;

/// <summary>
/// The object browser (PR-2.1 to PR-2.3, PR-2.7).
/// </summary>
/// <remarks>
/// One tree per connected instance, rooted at the database the connection opened.
/// DEC-7 designs for under ten instances, so there is no need for the tree to span
/// them — each connection gets its own root and they sit side by side.
/// </remarks>
public sealed partial class ObjectTreeViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ICatalogReader _catalog;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _includeSystemObjects;

    [ObservableProperty]
    private CatalogNodeViewModel? _selectedNode;

    public ObjectTreeViewModel(ConnectionDescriptor descriptor, ICatalogReader catalog)
    {
        Descriptor = descriptor;
        _catalog = catalog;

        Roots = BuildFolders();
    }

    public ConnectionDescriptor Descriptor { get; }

    /// <summary>
    /// The folders, one per object kind (PR-2.1).
    /// </summary>
    /// <remarks>
    /// Flat rather than nested under a database node: the connection already names
    /// the database, and one less level to expand is one less click on the way to
    /// the thing you wanted (PR-8.5).
    /// </remarks>
    public ObservableCollection<CatalogNodeViewModel> Roots { get; }

    /// <summary>The catalogue query behind whatever is selected, for PR-8.2.</summary>
    public string? SelectedQuery => SelectedNode?.SourceQuery;

    /// <summary>The selected object, when the selection is one.</summary>
    public SchemaObject? SelectedObject => (SelectedNode as SchemaObjectNodeViewModel)?.Object;

    private ObservableCollection<CatalogNodeViewModel> BuildFolders() =>
    [
        Folder("Tables", SchemaObjectKind.Table),
        Folder("Views", SchemaObjectKind.View),
        Folder("Synonyms", SchemaObjectKind.Synonym),
        Folder("Sequences", SchemaObjectKind.Sequence),
        Folder("Procedures", SchemaObjectKind.Procedure),
        Folder("Functions", SchemaObjectKind.Function),
        Folder("Indexes", SchemaObjectKind.Index),
        Folder("Types", SchemaObjectKind.UserDefinedType),
    ];

    private ObjectFolderNodeViewModel Folder(string name, SchemaObjectKind kind) =>
        new(name,
            kind,
            _catalog,
            () => string.IsNullOrWhiteSpace(Filter) ? null : Filter.Trim(),
            () => IncludeSystemObjects);

    /// <summary>Refreshes one subtree without rebuilding the whole tree (PR-2.7).</summary>
    [RelayCommand]
    public async Task RefreshNodeAsync(CatalogNodeViewModel? node)
    {
        CatalogNodeViewModel? target = node ?? SelectedNode;

        if (target is null)
        {
            return;
        }

        target.Invalidate();

        if (target.IsExpanded)
        {
            await target.EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Re-reads everything that is currently open.</summary>
    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        foreach (CatalogNodeViewModel root in Roots)
        {
            await RefreshNodeAsync(root).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Applies the name filter (PR-2.3).
    /// </summary>
    /// <remarks>
    /// The filter goes into the catalogue query rather than being applied to a
    /// loaded list, because with 20,000+ objects (NFR-2) the list is exactly what we
    /// are trying not to fetch.
    /// </remarks>
    [RelayCommand]
    public async Task ApplyFilterAsync()
    {
        foreach (CatalogNodeViewModel root in Roots.Where(r => r.IsExpanded))
        {
            root.Invalidate();
            await root.EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    partial void OnSelectedNodeChanged(CatalogNodeViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedQuery));
        OnPropertyChanged(nameof(SelectedObject));
    }

    partial void OnIncludeSystemObjectsChanged(bool value) =>
        _ = ApplyFilterAsync();

    public async ValueTask DisposeAsync()
    {
        if (_catalog is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
