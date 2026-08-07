namespace Ims.Core.Data;

/// <summary>
/// An error from the server, with the detail an Informix user actually needs.
/// </summary>
/// <remarks>
/// PR-3.6 asks for three things together: the Informix error code, the ISAM
/// error where there is one, and a plain-language explanation. Generic clients
/// typically surface only a driver message, which is precisely the gap that
/// sends people back to <c>dbaccess</c> — and the ISAM error is very often the
/// one that says what really went wrong.
/// </remarks>
public sealed record InformixError
{
    /// <summary>The Informix SQLCODE, negative for errors.</summary>
    public required int SqlCode { get; init; }

    /// <summary>The ISAM error code, where the server reported one.</summary>
    public int? IsamCode { get; init; }

    /// <summary>SQLSTATE, where the driver supplied one.</summary>
    public string? SqlState { get; init; }

    /// <summary>The server's or driver's own message text, unaltered (PR-8.2).</summary>
    public required string ServerMessage { get; init; }

    /// <summary>
    /// IMS's plain-language explanation, when it recognises the code. Null when it
    /// does not — an invented explanation would be worse than none.
    /// </summary>
    public string? Explanation { get; init; }

    /// <summary>Index of the failing statement in a multi-statement script (PR-3.4).</summary>
    public int? StatementIndex { get; init; }

    /// <summary>Character offset of the failing statement within the script (PR-3.4).</summary>
    public int? ScriptOffset { get; init; }

    /// <summary>
    /// True when the error means the connection is gone rather than the statement
    /// being wrong — the trigger for PR-1.7 reconnect handling.
    /// </summary>
    public bool IsConnectionLost { get; init; }

    /// <summary>True when the statement stopped because the user cancelled it (PR-3.5).</summary>
    public bool IsCancellation { get; init; }

    public override string ToString()
    {
        string code = IsamCode is { } isam and not 0
            ? $"SQLCODE {SqlCode}, ISAM {isam}"
            : $"SQLCODE {SqlCode}";

        return Explanation is null
            ? $"{code}: {ServerMessage}"
            : $"{code}: {ServerMessage} — {Explanation}";
    }
}

/// <summary>
/// Thrown when a server operation fails. Always carries a structured
/// <see cref="InformixError"/> so the UI never has to parse a message string.
/// </summary>
public sealed class InformixException : Exception
{
    public InformixException(InformixError error, Exception? innerException = null)
        : base(error.ToString(), innerException)
    {
        Error = error;
    }

    public InformixError Error { get; }
}
