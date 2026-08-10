namespace Ims.App.ViewModels;

/// <summary>
/// What the tab strip needs of anything it can show.
/// </summary>
/// <remarks>
/// The strip holds two kinds of document: an editor (<see cref="EditorTabViewModel"/>)
/// and an object's detail (<see cref="ObjectDetailTabViewModel"/>). They have almost
/// nothing in common — one has SQL, a session and results, the other a catalogue
/// read — so this stays deliberately small: a title for the header, and disposal for
/// the close path. Everything else is reached by narrowing to the concrete type, which
/// keeps the editor-only machinery (autosave, execute, cancel) from having to pretend
/// it applies to a detail tab.
/// </remarks>
public interface ITabViewModel : IAsyncDisposable
{
    /// <summary>The header text.</summary>
    string Title { get; }
}
