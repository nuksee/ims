namespace Ims.Core.Sql;

/// <summary>A reason IMS wants the user to confirm before sending a statement.</summary>
public sealed record StatementWarning
{
    public required string Title { get; init; }

    /// <summary>What will happen, in the user's terms.</summary>
    public required string Detail { get; init; }

    /// <summary>The PRD requirement this warning exists to satisfy.</summary>
    public required string Requirement { get; init; }
}

/// <summary>
/// Checks a statement for the one case PR-3.8 singles out.
/// </summary>
/// <remarks>
/// <para>
/// PR-3.8: "Warn before executing an <c>UPDATE</c> or <c>DELETE</c> with no
/// <c>WHERE</c> clause." Deliberately narrow. DEC-2 says IMS gates nothing —
/// Informix privileges are the real control, and RSK-7 notes this risk "is no worse
/// than <c>dbaccess</c> today". A tool that second-guesses every statement trains
/// people to click through warnings, which makes the one warning that matters
/// worthless.
/// </para>
/// <para>
/// So this warns, and does not block. The user's privileges decide what they may do.
/// </para>
/// </remarks>
public static class StatementSafety
{
    /// <summary>
    /// Returns a warning when the statement is an unqualified UPDATE or DELETE,
    /// otherwise null.
    /// </summary>
    public static StatementWarning? Check(string? sql)
    {
        // Analyse the statement with literals and comments removed, so neither a
        // WHERE inside a string nor one inside a comment can make it look qualified.
        string stripped = SqlText.StripLiteralsAndComments(sql);
        string keyword = SqlText.LeadingKeyword(stripped);

        if (keyword is not ("UPDATE" or "DELETE"))
        {
            return null;
        }

        if (SqlText.ContainsKeyword(stripped, "WHERE"))
        {
            return null;
        }

        // A positioned update or delete is bounded by its cursor, not by a WHERE.
        if (SqlText.ContainsKeyword(stripped, "CURRENT")
            && SqlText.ContainsKeyword(stripped, "OF"))
        {
            return null;
        }

        return keyword == "UPDATE"
            ? new StatementWarning
            {
                Title = "UPDATE with no WHERE clause",
                Detail = "This will update every row in the table.",
                Requirement = "PR-3.8",
            }
            : new StatementWarning
            {
                Title = "DELETE with no WHERE clause",
                Detail = "This will delete every row in the table.",
                Requirement = "PR-3.8",
            };
    }

    /// <summary>
    /// Checks a whole script, returning one entry per statement that needs
    /// confirmation. Empty when nothing does.
    /// </summary>
    public static IReadOnlyList<(SqlStatement Statement, StatementWarning Warning)> CheckScript(
        IEnumerable<SqlStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);

        var warnings = new List<(SqlStatement, StatementWarning)>();

        foreach (SqlStatement statement in statements)
        {
            if (Check(statement.Text) is { } warning)
            {
                warnings.Add((statement, warning));
            }
        }

        return warnings;
    }
}
