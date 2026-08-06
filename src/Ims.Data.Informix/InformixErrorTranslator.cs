using System.Data.Odbc;
using Ims.Core.Data;

namespace Ims.Data.Informix;

/// <summary>
/// Turns a driver exception into the error detail an Informix user needs.
/// </summary>
/// <remarks>
/// <para>
/// PR-3.6 asks for three things together: the Informix error code, the ISAM error
/// where there is one, and a plain-language explanation. Generic clients surface
/// only the driver's message, which is exactly the gap that sends people back to
/// <c>dbaccess</c> — and on Informix the ISAM error is very often the one that says
/// what actually went wrong.
/// </para>
/// <para>
/// ODBC reports Informix diagnostics as a list of records: the SQLCODE arrives as
/// the native error of the first, and the ISAM error, when there is one, as a
/// second record. That layering is what this class unpicks.
/// </para>
/// </remarks>
public static class InformixErrorTranslator
{
    /// <summary>SQLSTATEs that mean the connection is gone, not that the statement was wrong.</summary>
    private static readonly HashSet<string> ConnectionLostStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "08000", // connection exception
        "08001", // client unable to establish connection
        "08003", // connection does not exist
        "08S01", // communication link failure
        "HYT00", // timeout expired
        "HYT01", // connection timeout expired
    };

    /// <summary>Informix codes that mean the same.</summary>
    private static readonly HashSet<int> ConnectionLostCodes =
    [
        -908,   // attempt to connect to database server failed
        -930,   // cannot connect to database server
        -931,   // cannot open connection
        -25580, // system error occurred in network connection
        -27001, // read error occurred during connection attempt
    ];

    /// <summary>The user pressed cancel (PR-3.5), rather than anything going wrong.</summary>
    private static readonly HashSet<int> CancellationCodes = [-213, -409];

    /// <summary>
    /// Builds a structured error from a driver exception.
    /// </summary>
    /// <param name="exception">The driver exception.</param>
    /// <param name="statementIndex">Position in the script, for PR-3.4.</param>
    /// <param name="scriptOffset">Character offset in the script, for PR-3.4.</param>
    /// <param name="userCancelled">
    /// True when IMS asked for the cancel. Distinguishes a user-initiated stop from
    /// a server-side abort that happens to report the same code.
    /// </param>
    public static InformixError Translate(
        OdbcException exception,
        int? statementIndex = null,
        int? scriptOffset = null,
        bool userCancelled = false)
    {
        ArgumentNullException.ThrowIfNull(exception);

        int sqlCode = 0;
        int? isamCode = null;
        string? sqlState = null;
        var messages = new List<string>();

        for (int i = 0; i < exception.Errors.Count; i++)
        {
            OdbcError error = exception.Errors[i];
            messages.Add(error.Message.Trim());

            if (i == 0)
            {
                sqlCode = error.NativeError;
                sqlState = error.SQLState;
                continue;
            }

            // The first record carries the SQLCODE; a later non-zero native error is
            // the ISAM error. Keep the first one found, which is the specific one.
            if (isamCode is null && error.NativeError != 0)
            {
                isamCode = error.NativeError;
            }
        }

        bool cancelled = userCancelled
                         || CancellationCodes.Contains(sqlCode)
                         || string.Equals(sqlState, "HY008", StringComparison.OrdinalIgnoreCase);

        return new InformixError
        {
            SqlCode = sqlCode,
            IsamCode = isamCode,
            SqlState = sqlState,
            ServerMessage = messages.Count > 0
                ? string.Join(" ", messages)
                : exception.Message.Trim(),
            Explanation = cancelled ? "The statement was cancelled." : Explain(sqlCode, isamCode),
            StatementIndex = statementIndex,
            ScriptOffset = scriptOffset,
            IsConnectionLost = !cancelled && IsConnectionLost(sqlCode, sqlState),
            IsCancellation = cancelled,
        };
    }

    /// <summary>True when the error means the session is gone (PR-1.7).</summary>
    public static bool IsConnectionLost(int sqlCode, string? sqlState) =>
        ConnectionLostCodes.Contains(sqlCode)
        || (sqlState is not null && ConnectionLostStates.Contains(sqlState));

    /// <summary>
    /// A plain-language explanation for the codes IMS recognises.
    /// </summary>
    /// <remarks>
    /// Returns null when the code is unknown. PR-3.6 wants an explanation, and
    /// PR-8.4 says a half-implemented capability is worse than an absent one — an
    /// invented explanation for an unrecognised code would be exactly that, and the
    /// server's own message is still shown either way.
    /// </remarks>
    public static string? Explain(int sqlCode, int? isamCode = null)
    {
        // The ISAM error is usually the more specific of the two, so it wins.
        if (isamCode is { } isam && ExplainIsam(isam) is { } isamExplanation)
        {
            return isamExplanation;
        }

        return sqlCode switch
        {
            -201 => "The statement has a syntax error. The server stopped at the point named in "
                    + "its message.",
            -206 => "The table does not exist in this database. Check the spelling, the owner "
                    + "prefix, and whether you are connected to the database you meant.",
            -217 => "The column does not exist in any table in the statement. Check the spelling "
                    + "and the table it belongs to.",
            -236 => "The INSERT lists a different number of columns from the number of values "
                    + "supplied.",
            -239 => "A unique index would be violated: a row with this key already exists.",
            -268 => "A unique constraint would be violated: a row with this key already exists.",
            -271 => "The row could not be inserted. The ISAM error says why.",
            -284 => "A subquery that must return one row returned several. Add a condition that "
                    + "narrows it, or use IN rather than =.",
            -310 => "A table of that name already exists in this database.",
            -316 => "An index of that name already exists in this database.",
            -329 => "The database was not found, or you do not have permission to open it.",
            -349 => "No database is selected. Run DATABASE or CONNECT first.",
            -391 => "A column that does not allow nulls was given no value.",
            -692 => "The row cannot be deleted because another table still references it through "
                    + "a foreign key.",
            -908 => "The connection attempt was refused. The server may be down, or the host, "
                    + "port or protocol may be wrong.",
            -930 => "The server could not be reached. Check the host and service in the "
                    + "connection, and that the instance is online.",
            -951 => "The user name or password was not accepted by the server.",
            -1204 or -1205 or -1206 or -1212 =>
                "A date or datetime value could not be interpreted. Check the value against the "
                + "column's qualifier and the DBDATE setting.",
            -1213 => "A value that is not a number was used where a number was expected.",
            -1215 or -1226 => "A numeric value is too large for the column it is going into.",
            -11060 => "The ODBC driver rejected the connection string before trying to connect. "
                      + "This is a client-side problem, not a server one — most often a required "
                      + "keyword is missing rather than wrong, since the driver ignores keywords "
                      + "it does not recognise.",
            0 => null,
            _ => null,
        };
    }

    /// <summary>Explanations for the ISAM errors a user is most likely to hit.</summary>
    private static string? ExplainIsam(int isamCode) => isamCode switch
    {
        -107 => "Another session holds a lock on the row. Wait, or set a lock-mode wait for "
                + "your session.",
        -113 => "Another session holds a lock on the whole table.",
        -134 => "The server has no locks left. This is usually a sign of a statement locking far "
                + "more rows than intended.",
        -143 => "The session is deadlocked with another. One of the two had to be rolled back.",
        -144 => "The lock timed out while waiting for another session to release it.",
        -239 => "A duplicate value was supplied for a unique index.",
        -244 => "The row could not be read. This often means an index needs rebuilding.",
        -271 => null,
        _ => null,
    };
}
