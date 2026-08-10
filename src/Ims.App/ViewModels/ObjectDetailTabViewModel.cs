using CommunityToolkit.Mvvm.ComponentModel;
using Ims.Core.Catalog;

namespace Ims.App.ViewModels;

/// <summary>
/// One object's detail, as a tab of its own (PR-2.4).
/// </summary>
/// <remarks>
/// <para>
/// Detail used to be a third tab in the results area, sharing one
/// <see cref="TableDetailViewModel"/> with the tree. That put it where a statement's
/// output goes, and it retargeted itself as the tree selection moved — so it could
/// only ever show one object, and it changed under you. It is a document in its own
/// right, so it sits in the tab strip beside the editors instead, one tab per object.
/// </para>
/// <para>
/// The pane itself is unchanged: this wraps <see cref="TableDetailViewModel"/> rather
/// than reimplementing it, and the view binds straight to <see cref="Detail"/>.
/// </para>
/// <para>
/// Loading happens once, when the tab opens. That is what the old
/// <c>IsDetailVisible</c> gate was protecting — PR-6.4 asks that metadata queries stay
/// negligible on a production instance, and a pane that followed the tree issued six
/// catalogue queries per arrow key. A tab does not follow anything, so the gate is no
/// longer needed to get the same result.
/// </para>
/// </remarks>
public sealed partial class ObjectDetailTabViewModel : ObservableObject, ITabViewModel
{
    public ObjectDetailTabViewModel(SchemaObject subject, ICatalogReader catalog)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Detail = new TableDetailViewModel(catalog);
    }

    /// <summary>The object this tab is about. Fixed for the life of the tab.</summary>
    public SchemaObject Subject { get; }

    /// <summary>The pane's own view model, which the detail template binds to.</summary>
    public TableDetailViewModel Detail { get; }

    /// <summary>
    /// The header text: the bare name, not the qualified one.
    /// </summary>
    /// <remarks>
    /// The strip elides at 180px and puts the full title on the tooltip, so a qualified
    /// name would spend that budget on an owner prefix every tab shares. The qualified
    /// name is the heading inside the pane.
    /// </remarks>
    public string Title => Subject.Name;

    /// <summary>Reads the catalogue for this object.</summary>
    public Task LoadAsync(CancellationToken cancellationToken) =>
        Detail.ShowAsync(Subject, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
