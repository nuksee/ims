using System.Text;

namespace Ims.Core.Sql;

/// <summary>
/// Text utilities shared by the parts of IMS that must reason about a statement
/// without executing it.
/// </summary>
public static class SqlText
{
    /// <summary>
    /// Returns the statement with comments removed and every literal replaced by a
    /// placeholder, so keyword analysis cannot be fooled by content.
    /// </summary>
    /// <remarks>
    /// This is what stops <c>DELETE FROM audit -- no WHERE here on purpose</c> from
    /// being read as safe, and stops <c>UPDATE t SET note = 'see WHERE clause'</c>
    /// from being read as having one. Both matter for PR-3.8, where a false negative
    /// means a table quietly loses every row.
    /// </remarks>
    public static string StripLiteralsAndComments(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(sql.Length);
        int index = 0;

        while (index < sql.Length)
        {
            char current = sql[index];

            if (current == '\'' || current == '"')
            {
                // Keep a placeholder so token adjacency is preserved.
                builder.Append(current == '\'' ? "''" : "\"\"");
                index = SkipQuoted(sql, index, current);
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                int newline = sql.IndexOf('\n', index);
                builder.Append(' ');
                index = newline < 0 ? sql.Length : newline;
                continue;
            }

            if (current == '{')
            {
                int close = sql.IndexOf('}', index);
                builder.Append(' ');
                index = close < 0 ? sql.Length : close + 1;
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                int close = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                builder.Append(' ');
                index = close < 0 ? sql.Length : close + 2;
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when <paramref name="sql"/> contains <paramref name="word"/> as a whole
    /// word, ignoring case. Literals and comments should be stripped first.
    /// </summary>
    public static bool ContainsKeyword(string? sql, string word)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);

        if (string.IsNullOrEmpty(sql))
        {
            return false;
        }

        int index = 0;

        while ((index = sql.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk = index == 0 || !IsIdentifierChar(sql[index - 1]);
            int after = index + word.Length;
            bool rightOk = after >= sql.Length || !IsIdentifierChar(sql[after]);

            if (leftOk && rightOk)
            {
                return true;
            }

            index = after;
        }

        return false;
    }

    /// <summary>The first keyword of a statement, upper-cased, or empty.</summary>
    public static string LeadingKeyword(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        int start = 0;
        while (start < sql.Length && !char.IsLetter(sql[start]))
        {
            start++;
        }

        int end = start;
        while (end < sql.Length && IsIdentifierChar(sql[end]))
        {
            end++;
        }

        return end > start ? sql[start..end].ToUpperInvariant() : string.Empty;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
