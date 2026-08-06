namespace Ims.Core.Sql;

/// <summary>One statement carved out of a script.</summary>
/// <param name="Text">The statement, trimmed, without its terminating semicolon.</param>
/// <param name="Offset">Character offset of the statement within the original script.</param>
/// <param name="LineNumber">1-based line the statement starts on.</param>
public readonly record struct SqlStatement(string Text, int Offset, int LineNumber);

/// <summary>
/// Splits a script into the statements IMS will send, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// PR-3.4 requires a multi-statement script to present each result or error in
/// sequence, "indicating clearly which statement failed" — which is why each
/// statement keeps its offset and line number, not just its text.
/// </para>
/// <para>
/// The hard case on Informix is SPL. A stored procedure body is full of semicolons
/// that are not statement terminators, so a naive split on <c>;</c> tears
/// <c>CREATE PROCEDURE</c> into fragments that each fail on their own. Getting this
/// wrong is the single most visible way a SQL tool announces it does not really
/// understand the platform, so the splitter tracks routine bodies and terminates
/// them only after <c>END PROCEDURE</c> or <c>END FUNCTION</c>.
/// </para>
/// <para>
/// It also honours all three of Informix's comment forms — <c>--</c> to end of
/// line, <c>{ }</c> braces, and <c>/* */</c> — plus quoted strings with doubled-quote
/// escaping and delimited identifiers.
/// </para>
/// </remarks>
public static class SqlStatementSplitter
{
    /// <summary>Splits a script. Returns an empty list for a script with no statements.</summary>
    public static IReadOnlyList<SqlStatement> Split(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return [];
        }

        var statements = new List<SqlStatement>();

        int length = script.Length;
        int statementStart = 0;
        int index = 0;

        bool inRoutineBody = false;
        bool routineEnded = false;
        bool statementStartKnown = false;

        while (index < length)
        {
            char current = script[index];

            // Skip anything that can contain a semicolon without meaning one.
            if (current == '\'')
            {
                index = SkipQuoted(script, index, '\'');
                statementStartKnown = true;
                continue;
            }

            if (current == '"')
            {
                index = SkipQuoted(script, index, '"');
                statementStartKnown = true;
                continue;
            }

            if (current == '-' && index + 1 < length && script[index + 1] == '-')
            {
                index = SkipToLineEnd(script, index);
                continue;
            }

            if (current == '{')
            {
                index = SkipTo(script, index + 1, "}");
                continue;
            }

            if (current == '/' && index + 1 < length && script[index + 1] == '*')
            {
                index = SkipTo(script, index + 2, "*/");
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            // First non-trivial character of a statement: decide whether this is a
            // routine definition, whose body's semicolons must be left alone.
            if (!statementStartKnown)
            {
                statementStartKnown = true;
                inRoutineBody = IsRoutineDefinition(script, index);
                routineEnded = false;
            }

            if (current == ';')
            {
                if (inRoutineBody && !routineEnded)
                {
                    // A semicolon inside an SPL body. Not a terminator.
                    index++;
                    continue;
                }

                AddStatement(statements, script, statementStart, index);

                index++;
                statementStart = index;
                statementStartKnown = false;
                inRoutineBody = false;
                routineEnded = false;
                continue;
            }

            if (inRoutineBody
                && !routineEnded
                && (current is 'E' or 'e')
                && MatchesRoutineEnd(script, index))
            {
                routineEnded = true;
            }

            index++;
        }

        // Whatever is left after the last semicolon, if it is more than whitespace.
        AddStatement(statements, script, statementStart, length);

        return statements;
    }

    private static void AddStatement(
        List<SqlStatement> statements,
        string script,
        int start,
        int end)
    {
        if (end <= start)
        {
            return;
        }

        string raw = script[start..end];

        // A run of comments and whitespace is not a statement, but it must not be
        // dropped from the offsets of the statements that follow it.
        if (IsOnlyTriviaOrEmpty(raw))
        {
            return;
        }

        int leading = 0;
        while (leading < raw.Length && char.IsWhiteSpace(raw[leading]))
        {
            leading++;
        }

        string text = raw.Trim();

        if (text.Length == 0)
        {
            return;
        }

        int offset = start + leading;

        statements.Add(new SqlStatement(text, offset, LineNumberAt(script, offset)));
    }

    /// <summary>
    /// True when the text holds nothing a server would act on — whitespace and
    /// comments only.
    /// </summary>
    private static bool IsOnlyTriviaOrEmpty(string text)
    {
        int index = 0;

        while (index < text.Length)
        {
            char current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                index = SkipToLineEnd(text, index);
                continue;
            }

            if (current == '{')
            {
                index = SkipTo(text, index + 1, "}");
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index = SkipTo(text, index + 2, "*/");
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Recognises the statements whose body contains semicolons that are not
    /// terminators: CREATE/ALTER PROCEDURE and CREATE/ALTER FUNCTION, including the
    /// DBA and OR REPLACE variants.
    /// </summary>
    private static bool IsRoutineDefinition(string script, int start)
    {
        ReadOnlySpan<char> span = script.AsSpan(start);

        if (!StartsWithWord(span, "CREATE") && !StartsWithWord(span, "ALTER"))
        {
            return false;
        }

        // Look a short way ahead for PROCEDURE or FUNCTION, past the optional
        // OR REPLACE / DBA qualifiers. Bounded, so a long script costs nothing.
        int limit = Math.Min(span.Length, 64);
        ReadOnlySpan<char> window = span[..limit];

        return ContainsWord(window, "PROCEDURE") || ContainsWord(window, "FUNCTION");
    }

    /// <summary>Matches <c>END PROCEDURE</c> or <c>END FUNCTION</c> at this position.</summary>
    private static bool MatchesRoutineEnd(string script, int index)
    {
        ReadOnlySpan<char> span = script.AsSpan(index);

        if (!StartsWithWord(span, "END"))
        {
            return false;
        }

        int after = 3;
        while (after < span.Length && char.IsWhiteSpace(span[after]))
        {
            after++;
        }

        ReadOnlySpan<char> rest = span[after..];

        return StartsWithWord(rest, "PROCEDURE") || StartsWithWord(rest, "FUNCTION");
    }

    private static bool StartsWithWord(ReadOnlySpan<char> span, string word)
    {
        if (span.Length < word.Length
            || !span[..word.Length].Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Must be a whole word: CREATED is not CREATE.
        return span.Length == word.Length || !IsIdentifierChar(span[word.Length]);
    }

    private static bool ContainsWord(ReadOnlySpan<char> span, string word)
    {
        for (int i = 0; i + word.Length <= span.Length; i++)
        {
            if (i > 0 && IsIdentifierChar(span[i - 1]))
            {
                continue;
            }

            if (StartsWithWord(span[i..], word))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Skips a quoted run, honouring the doubled-quote escape Informix uses for
    /// both string literals and delimited identifiers.
    /// </summary>
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

            // A doubled quote is an escaped quote, not the end of the run.
            if (index + 1 < text.Length && text[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        // Unterminated. Consume the rest rather than looping — the server will
        // report the syntax error, which is the right place for it (PR-8.2).
        return text.Length;
    }

    private static int SkipToLineEnd(string text, int start)
    {
        int newline = text.IndexOf('\n', start);
        return newline < 0 ? text.Length : newline + 1;
    }

    private static int SkipTo(string text, int start, string terminator)
    {
        int found = text.IndexOf(terminator, start, StringComparison.Ordinal);
        return found < 0 ? text.Length : found + terminator.Length;
    }

    private static int LineNumberAt(string text, int offset)
    {
        int line = 1;

        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
