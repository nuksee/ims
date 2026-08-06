using Ims.Core.Sql;

namespace Ims.Core.Completion;

/// <summary>What the caret is asking for.</summary>
public enum CompletionTarget
{
    /// <summary>After FROM, JOIN, INTO or UPDATE: a table, view or synonym.</summary>
    ObjectName,

    /// <summary>Inside SELECT, WHERE, ON, SET, GROUP BY, HAVING or ORDER BY.</summary>
    ColumnOrExpression,

    /// <summary>After a dot. What follows depends entirely on what precedes it.</summary>
    Member,

    /// <summary>Start of a statement, or somewhere nothing narrows it down.</summary>
    Anything,
}

/// <summary>A table named in the statement, with the alias it was given.</summary>
public sealed record TableReference(string Name, string? Owner, string? Alias)
{
    /// <summary>What the user would write in front of a column: the alias if there is one.</summary>
    public string Qualifier => Alias ?? Name;
}

/// <summary>
/// What the caret is next to, worked out from the statement around it (PR-3.2).
/// </summary>
/// <param name="Target">What kind of thing belongs here.</param>
/// <param name="Prefix">The partial word being typed, possibly empty.</param>
/// <param name="ReplacementOffset">Where <paramref name="Prefix"/> starts in the script.</param>
/// <param name="Qualifier">
/// The text before the dot, when <paramref name="Target"/> is
/// <see cref="CompletionTarget.Member"/>. An alias, a table name, or an owner.
/// </param>
/// <param name="Tables">Every table the statement names, wherever the caret sits in it.</param>
public sealed record CompletionContext(
    CompletionTarget Target,
    string Prefix,
    int ReplacementOffset,
    string? Qualifier,
    IReadOnlyList<TableReference> Tables)
{
    /// <summary>
    /// Reads the caret's surroundings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The statement is found first, not the whole script: a caret in the third
    /// statement of a script should not be offered the tables from the first.
    /// </para>
    /// <para>
    /// Tables are collected from the <em>whole</em> statement rather than only the
    /// part before the caret. In <c>SELECT |&#160;FROM customer</c> the caret is in
    /// the select list and the table is three words to its right — which is exactly
    /// the order people type in, and refusing to look ahead would make completion
    /// useless in the commonest case there is.
    /// </para>
    /// </remarks>
    public static CompletionContext Analyse(string? script, int caret)
    {
        script ??= string.Empty;
        caret = Math.Clamp(caret, 0, script.Length);

        (string statement, int statementOffset) = StatementAround(script, caret);

        // The splitter trims, so a caret in the whitespace after the last word is past
        // the end of its own statement. That is the normal position for someone about
        // to type, not an edge case: clamp for the analysis, but keep the real offset
        // for the replacement, or the completion window would swallow the whitespace.
        int rawCaret = caret - statementOffset;
        bool beyond = rawCaret > statement.Length;
        int localCaret = Math.Clamp(rawCaret, 0, statement.Length);

        IReadOnlyList<SqlToken> tokens = SqlTokenizer.Tokenize(statement);

        (string prefix, int prefixOffset) = beyond
            ? (string.Empty, localCaret)
            : PrefixAt(statement, localCaret);

        int replacementOffset = beyond ? caret : prefixOffset + statementOffset;

        IReadOnlyList<TableReference> tables = CollectTables(tokens);

        // A dot immediately before the word being typed changes everything: nothing
        // but that qualifier's members can be correct there.
        if (!beyond && QualifierBefore(statement, prefixOffset) is { } qualifier)
        {
            return new CompletionContext(
                CompletionTarget.Member, prefix, replacementOffset, qualifier, tables);
        }

        CompletionTarget target = TargetFor(tokens, beyond ? statement.Length + 1 : prefixOffset);

        return new CompletionContext(target, prefix, replacementOffset, null, tables);
    }

    /// <summary>The statement the caret sits in, and where it starts in the script.</summary>
    private static (string Text, int Offset) StatementAround(string script, int caret)
    {
        IReadOnlyList<SqlStatement> statements = SqlStatementSplitter.Split(script);

        SqlStatement? found = null;

        foreach (SqlStatement statement in statements)
        {
            // The last statement that begins at or before the caret, rather than the
            // one that contains it. The splitter trims, so a caret sitting in the
            // whitespace after "… where " is past the end of its own statement — and
            // that whitespace is exactly where someone about to type is standing.
            if (statement.Offset > caret)
            {
                break;
            }

            found = statement;
        }

        // Before the first statement, or a script the splitter found nothing in. The
        // whole text is the honest fallback, and it costs only a wider table list.
        return found is { } match ? (match.Text, match.Offset) : (script, 0);
    }

    /// <summary>The partial word at the caret, and where it begins.</summary>
    private static (string Prefix, int Offset) PrefixAt(string statement, int caret)
    {
        int start = caret;

        while (start > 0 && IsIdentifierChar(statement[start - 1]))
        {
            start--;
        }

        return (statement[start..caret], start);
    }

    /// <summary>
    /// The qualifier before a dot, or null when the word is not a member reference.
    /// </summary>
    /// <remarks>
    /// Handles two levels — <c>owner.table.</c> — because Informix names are
    /// owner-qualified and a developer typing one has not finished after the first dot.
    /// </remarks>
    private static string? QualifierBefore(string statement, int prefixOffset)
    {
        int index = prefixOffset - 1;

        if (index < 0 || statement[index] != '.')
        {
            return null;
        }

        int end = index;
        int start = end;

        while (start > 0 && (IsIdentifierChar(statement[start - 1]) || statement[start - 1] == '"'))
        {
            start--;
        }

        // A second dot: the first part is an owner, the second the object.
        if (start > 0 && statement[start - 1] == '.')
        {
            int outerEnd = start - 1;
            int outerStart = outerEnd;

            while (outerStart > 0
                   && (IsIdentifierChar(statement[outerStart - 1]) || statement[outerStart - 1] == '"'))
            {
                outerStart--;
            }

            start = outerStart;
        }

        string qualifier = statement[start..end].Trim();

        return qualifier.Length == 0 ? null : qualifier;
    }

    /// <summary>
    /// Which clause the caret is in, from the last clause keyword before it.
    /// </summary>
    /// <remarks>
    /// Deliberately not a parser. A completion list is a suggestion, and a suggestion
    /// that is right most of the time and cheap to compute beats one that is right
    /// always and stalls the caret — NFR-1 and PR-8.5 apply to typing more than to
    /// anything else IMS does.
    /// </remarks>
    private static CompletionTarget TargetFor(IReadOnlyList<SqlToken> tokens, int caret)
    {
        string last = string.Empty;
        string previous = string.Empty;

        foreach (SqlToken token in tokens)
        {
            if (token.Offset >= caret || token.Kind != SqlTokenKind.Word)
            {
                continue;
            }

            string word = token.Text.ToUpperInvariant();

            if (!ClauseKeywords.Contains(word))
            {
                continue;
            }

            previous = last;
            last = word;
        }

        return last switch
        {
            // "DELETE FROM" and "INSERT INTO" both want an object, and so does the bare
            // FROM of a SELECT. They are the same question.
            "FROM" or "JOIN" or "INTO" or "UPDATE" or "TABLE" => CompletionTarget.ObjectName,

            "SELECT" or "WHERE" or "ON" or "SET" or "HAVING" or "BY" or "VALUES" or "USING" =>
                CompletionTarget.ColumnOrExpression,

            // GROUP and ORDER are only clause starts with BY after them; alone they are
            // most likely still being typed.
            "GROUP" or "ORDER" => previous.Length == 0
                ? CompletionTarget.Anything
                : CompletionTarget.ColumnOrExpression,

            _ => CompletionTarget.Anything,
        };
    }

    /// <summary>
    /// Every table the statement names, with its alias.
    /// </summary>
    /// <remarks>
    /// Reads the comma-separated list after each FROM, JOIN, UPDATE or INTO, stopping
    /// at the next clause keyword. Informix's old-style <c>OUTER(table)</c> is handled
    /// by ignoring the brackets rather than by special-casing it.
    /// </remarks>
    private static List<TableReference> CollectTables(IReadOnlyList<SqlToken> tokens)
    {
        var tables = new List<TableReference>();

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Word)
            {
                continue;
            }

            string word = tokens[i].Text.ToUpperInvariant();

            if (word is not ("FROM" or "JOIN" or "UPDATE" or "INTO"))
            {
                continue;
            }

            i = ReadTableList(tokens, i + 1, tables);
        }

        return tables;
    }

    private static int ReadTableList(
        IReadOnlyList<SqlToken> tokens,
        int index,
        List<TableReference> tables)
    {
        while (index < tokens.Count)
        {
            SqlToken token = tokens[index];

            // OUTER(t) and any other bracketing: step over the punctuation and carry on.
            if (token.Kind == SqlTokenKind.Punctuation && token.Text is "(" or ")" or ",")
            {
                index++;
                continue;
            }

            if (token.Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier))
            {
                return index;
            }

            if (token.Kind == SqlTokenKind.Word
                && ClauseKeywords.Contains(token.Text.ToUpperInvariant())
                && !token.Text.Equals("OUTER", StringComparison.OrdinalIgnoreCase))
            {
                // The list has ended. Step back so the caller sees this keyword too — a
                // JOIN right after a FROM list starts another list of its own.
                return index - 1;
            }

            if (token.Text.Equals("OUTER", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            (TableReference table, int next) = ReadOneTable(tokens, index);
            tables.Add(table);
            index = next;
        }

        return index;
    }

    private static (TableReference Table, int Next) ReadOneTable(
        IReadOnlyList<SqlToken> tokens,
        int index)
    {
        string first = tokens[index].Identifier;
        string? owner = null;
        string name = first;
        index++;

        // owner.table, and Informix's database@server:owner.table reduced to its tail.
        while (index + 1 < tokens.Count
               && tokens[index].Kind == SqlTokenKind.Punctuation
               && tokens[index].Text is "." or ":"
               && tokens[index + 1].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)
        {
            owner = name;
            name = tokens[index + 1].Identifier;
            index += 2;
        }

        string? alias = null;

        if (index < tokens.Count
            && tokens[index].Kind == SqlTokenKind.Word
            && tokens[index].Text.Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index < tokens.Count
            && tokens[index].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier
            && !ClauseKeywords.Contains(tokens[index].Text.ToUpperInvariant()))
        {
            alias = tokens[index].Identifier;
            index++;
        }

        return (new TableReference(name, owner, alias), index);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>
    /// The words that end one clause and begin the next.
    /// </summary>
    /// <remarks>
    /// LEFT, RIGHT, FULL and INNER are here so that a table list stops at them rather
    /// than swallowing them as an alias — <c>FROM a LEFT JOIN b</c> must not decide
    /// that <c>a</c> is aliased <c>LEFT</c>.
    /// </remarks>
    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.Ordinal)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "BY", "HAVING", "UNION", "INTERSECT",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON", "USING",
        "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE", "TABLE",
        "AND", "OR", "NOT", "AS", "WITH", "CREATE", "ALTER", "DROP", "EXISTS",
        "LIMIT", "FIRST", "SKIP", "DISTINCT", "ALL", "TEMP",
    };
}
