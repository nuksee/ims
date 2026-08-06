namespace Ims.Core.Completion;

/// <summary>What a suggestion is, which decides its glyph and how it sorts.</summary>
public enum CompletionKind
{
    /// <summary>An alias declared in the statement being typed.</summary>
    Alias,

    Column,
    Table,
    View,
    Synonym,
    Sequence,

    /// <summary>A stored procedure or function.</summary>
    Routine,

    /// <summary>An Informix built-in function.</summary>
    BuiltInFunction,

    DataType,
    Keyword,
    Owner,
}

/// <summary>One suggestion (PR-3.2).</summary>
/// <param name="Text">What gets inserted.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Detail">
/// A short line shown beside the item. For Informix-specific syntax this is where
/// PR-8.3 pays off — <c>MATCHES</c> is only useful if you are told it is not
/// <c>LIKE</c>.
/// </param>
public sealed record CompletionItem(string Text, CompletionKind Kind, string? Detail = null)
{
    /// <summary>A text glyph, so the list needs no icon assets.</summary>
    public string Glyph => Kind switch
    {
        CompletionKind.Alias => "α",
        CompletionKind.Column => "▪",
        CompletionKind.Table => "▦",
        CompletionKind.View => "◫",
        CompletionKind.Synonym => "↪",
        CompletionKind.Sequence => "#",
        CompletionKind.Routine => "⚙",
        CompletionKind.BuiltInFunction => "ƒ",
        CompletionKind.DataType => "τ",
        CompletionKind.Owner => "@",
        _ => "▸",
    };

    /// <summary>
    /// Sort weight, lower first.
    /// </summary>
    /// <remarks>
    /// What the caret is next to decides the order, not the alphabet. Inside a WHERE
    /// clause the columns of the tables already named are almost always what is
    /// wanted, and a list that puts <c>ABS</c> above them is a list you scroll past.
    /// </remarks>
    public int Rank => Kind switch
    {
        CompletionKind.Alias => 0,
        CompletionKind.Column => 1,
        CompletionKind.Table or CompletionKind.View or CompletionKind.Synonym => 2,
        CompletionKind.Routine or CompletionKind.Sequence => 3,
        CompletionKind.BuiltInFunction => 4,
        CompletionKind.DataType => 5,
        _ => 6,
    };
}
