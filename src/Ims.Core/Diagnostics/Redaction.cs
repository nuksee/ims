using System.Text.RegularExpressions;

namespace Ims.Core.Diagnostics;

/// <summary>
/// Strips secrets and user data out of text bound for a log file.
/// </summary>
/// <remarks>
/// <para>
/// PR-6.3: "Never write credentials, tokens or result-set data into application
/// logs." NFR-10 still wants logs useful for debugging, so the aim is to remove
/// the sensitive part and keep the diagnostic shape.
/// </para>
/// <para>
/// Applied at the logging boundary by <see cref="RedactingLoggerProvider"/> rather
/// than at each call site, because a rule enforced at one place holds and a rule
/// enforced at two hundred does not.
/// </para>
/// </remarks>
public static partial class Redaction
{
    /// <summary>What replaces a removed secret.</summary>
    public const string Marker = "***";

    /// <summary>
    /// Masks password-bearing keys in an ODBC or connection-string-shaped fragment.
    /// </summary>
    public static string ConnectionString(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : PasswordKeyPattern().Replace(text, $"$1={Marker}");

    /// <summary>
    /// Makes a statement safe to log: string and numeric literals are removed,
    /// because a literal can be a patient identifier, a password being set, or any
    /// other value PR-6.3 exists to keep out of a log file.
    /// </summary>
    /// <remarks>
    /// The statement's structure survives, which is what makes a log entry useful
    /// for diagnosis. The full text is still available to the user in query history
    /// (PR-3.12) — that is local, user-visible, and explicitly permitted by DEC-8.
    /// </remarks>
    public static string Sql(string? sql, int maxLength = 512)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        string masked = StringLiteralPattern().Replace(sql, $"'{Marker}'");
        masked = NumericLiteralPattern().Replace(masked, Marker);
        masked = WhitespacePattern().Replace(masked, " ").Trim();

        return masked.Length <= maxLength
            ? masked
            : string.Concat(masked.AsSpan(0, maxLength), "… [truncated]");
    }

    /// <summary>
    /// Describes a result cell without disclosing it. Result-set data never reaches
    /// a log; only its shape does.
    /// </summary>
    public static string ResultValue(bool isNull, string? typeName) =>
        isNull ? $"<null:{typeName ?? "?"}>" : $"<value:{typeName ?? "?"}>";

    /// <summary>
    /// Final sweep over an already-formatted log message, catching anything a call
    /// site interpolated in by hand.
    /// </summary>
    public static string Message(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return PasswordKeyPattern().Replace(message, $"$1={Marker}");
    }

    // PWD, PASSWORD, PASSWD, SECRET, TOKEN — with or without spaces around '='.
    //
    // The value runs to the next ';' or end of line, NOT to the next space. A
    // connection-string value may legitimately contain spaces, and a password
    // certainly may. Stopping at whitespace leaked the tail of one during a real
    // smoke-test run — under-redaction is a breach, over-redaction is only a
    // less useful log line, so this errs the safe way.
    [GeneratedRegex(
        @"\b(PWD|PASSWORD|PASSWD|SECRET|TOKEN|APIKEY|API_KEY)\b\s*=\s*[^;\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordKeyPattern();

    // Single-quoted SQL literals, honouring Informix's doubled-quote escape.
    [GeneratedRegex(
        @"'(?:[^']|'')*'",
        RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteralPattern();

    // Bare numeric literals, but not identifiers that merely contain digits.
    [GeneratedRegex(
        @"(?<![\w.])\d+(?:\.\d+)?(?![\w.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteralPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
