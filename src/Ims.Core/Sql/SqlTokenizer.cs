namespace Ims.Core.Sql;

/// <summary>What a token is, to the extent completion needs to care.</summary>
public enum SqlTokenKind
{
    /// <summary>An identifier or keyword. Which of the two is a question of context.</summary>
    Word,

    /// <summary>A delimited identifier — <c>"Mixed Case"</c> — with its quotes kept.</summary>
    QuotedIdentifier,

    Number,

    /// <summary>A string literal, quotes included.</summary>
    String,

    /// <summary>Any of Informix's three comment forms.</summary>
    Comment,

    Punctuation,
}

/// <summary>One token, with its position in the original text.</summary>
public readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Offset)
{
    public int End => Offset + Text.Length;

    /// <summary>The identifier this token names, without its delimiters.</summary>
    public string Identifier => Kind == SqlTokenKind.QuotedIdentifier && Text.Length >= 2
        ? Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
        : Text;
}

/// <summary>
/// Splits SQL into tokens, keeping every offset.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SqlText.StripLiteralsAndComments"/> answers "does this statement
/// contain a WHERE", which is all PR-3.8 needs. Completion needs more: it has to
/// know <em>where</em> the caret is, so it cannot use a transform that changes the
/// length of the text. Hence a real scanner, however small.
/// </para>
/// <para>
/// Whitespace is dropped rather than tokenised. Nothing downstream needs it, and its
/// absence is what makes "the token before the caret" a useful question to ask.
/// </para>
/// </remarks>
public static class SqlTokenizer
{
    public static IReadOnlyList<SqlToken> Tokenize(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return [];
        }

        var tokens = new List<SqlToken>();
        int index = 0;

        while (index < sql.Length)
        {
            char current = sql[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            // Informix's three comment forms. A completion engine that reads a keyword
            // inside a comment as a clause is worse than one that offers nothing.
            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                int newline = sql.IndexOf('\n', index);
                int end = newline < 0 ? sql.Length : newline;
                tokens.Add(new SqlToken(SqlTokenKind.Comment, sql[index..end], index));
                index = end;
                continue;
            }

            if (current == '{')
            {
                int close = sql.IndexOf('}', index);
                int end = close < 0 ? sql.Length : close + 1;
                tokens.Add(new SqlToken(SqlTokenKind.Comment, sql[index..end], index));
                index = end;
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                int close = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                int end = close < 0 ? sql.Length : close + 2;
                tokens.Add(new SqlToken(SqlTokenKind.Comment, sql[index..end], index));
                index = end;
                continue;
            }

            if (current is '\'' or '"')
            {
                int end = SkipQuoted(sql, index, current);

                tokens.Add(new SqlToken(
                    current == '\'' ? SqlTokenKind.String : SqlTokenKind.QuotedIdentifier,
                    sql[index..end],
                    index));

                index = end;
                continue;
            }

            if (char.IsDigit(current))
            {
                int end = index;

                while (end < sql.Length && (char.IsDigit(sql[end]) || sql[end] == '.'))
                {
                    end++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[index..end], index));
                index = end;
                continue;
            }

            if (IsIdentifierStart(current))
            {
                int end = index;

                while (end < sql.Length && IsIdentifierChar(sql[end]))
                {
                    end++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.Word, sql[index..end], index));
                index = end;
                continue;
            }

            tokens.Add(new SqlToken(SqlTokenKind.Punctuation, sql[index..(index + 1)], index));
            index++;
        }

        return tokens;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static int SkipQuoted(string text, int start, char quote)
    {
        int index = start + 1;

        while (index < text.Length)
        {
            if (text[index] != quote)
            {
                index++;
                continue;
            }

            // Informix escapes a quote by doubling it.
            if (index + 1 < text.Length && text[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }
}
