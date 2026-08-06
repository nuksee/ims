using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Ims.Core.Completion;

namespace Ims.App.Editing;

/// <summary>
/// One row in the completion list (PR-3.2).
/// </summary>
/// <remarks>
/// AvalonEdit sorts by <see cref="Priority"/> descending, so the engine's rank —
/// where lower is better — is negated on the way in. Doing the inversion here rather
/// than in the engine keeps <see cref="CompletionItem.Rank"/> readable, and keeps
/// AvalonEdit's conventions out of <c>Ims.Core</c> (NFR-5).
/// </remarks>
internal sealed class SqlCompletionData(CompletionItem item, int order) : ICompletionData
{
    public ImageSource? Image => null;

    public string Text => item.Text;

    /// <summary>What the row shows: the glyph, then the word.</summary>
    public object Content => $"{item.Glyph}  {item.Text}";

    /// <summary>
    /// The tooltip. PR-8.3 lives here — this is where an Informix-specific keyword
    /// gets to say what makes it Informix-specific.
    /// </summary>
    public object Description => item.Detail is { Length: > 0 } detail
        ? $"{item.Kind}\n{detail}"
        : item.Kind.ToString();

    public double Priority => -order;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, item.Text);
}
