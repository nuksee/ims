using System.Data.Odbc;
using Ims.Core.Connections;

namespace Ims.Data.Informix;

/// <summary>
/// Builds the DSN-less ODBC connection string IMS uses to reach an instance.
/// </summary>
/// <remarks>
/// <para>
/// DSN-less on purpose. A DSN is machine state that has to be provisioned
/// separately, and DEC-7 plus PR-1.2 already put the instance list inside IMS —
/// requiring a matching set of DSNs would be a second inventory to keep in step.
/// </para>
/// <para>
/// The keyword set mirrors <c>sqlhosts</c> semantics (PR-1.1): Server, Host,
/// Service and Protocol are passed individually rather than relying on the
/// machine's own <c>sqlhosts</c>, so a connection IMS holds is fully described by
/// its own descriptor.
/// </para>
/// </remarks>
public static class InformixOdbcConnectionString
{
    /// <summary>
    /// Builds a connection string for <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="descriptor">The connection to describe. Carries no secret (DEC-9).</param>
    /// <param name="driverName">
    /// The registered driver name, discovered by <see cref="CsdkLocator"/>. Passed in
    /// rather than hard-coded because it carries a bitness suffix that varies by build.
    /// </param>
    /// <param name="password">
    /// Resolved from Windows Credential Manager at the moment of use. Never stored on
    /// the descriptor and never logged (PR-6.3) — use <see cref="ForLogging"/> if a
    /// connection string must appear in a diagnostic.
    /// </param>
    public static string Build(
        ConnectionDescriptor descriptor,
        string driverName,
        string? password)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        var builder = new OdbcConnectionStringBuilder
        {
            Driver = driverName,
        };

        builder["Server"] = descriptor.ServerName;
        builder["Host"] = descriptor.Host;
        builder["Service"] = descriptor.Service;
        builder["Protocol"] = descriptor.Protocol;

        // The Database keyword must be PRESENT, even when empty. Omitting it makes
        // the CSDK ODBC driver fail with -11060 "General error" before it attempts
        // any network I/O — which reads as a connection problem and is not one.
        // Measured against the 4.10 driver: no Database => -11060; Database= (empty)
        // => -908, i.e. a real connection attempt. Connecting at instance level
        // rather than to a named database is legitimate, so an empty value is right.
        //
        // But "the driver attempts it" is not "the server allows it". Measured against
        // demo_srv (14.10) on 2026-08-06, an empty Database is refused with
        // -354 "Incorrect database or cursor name format", and the same connection
        // succeeds the moment a database is named. So emitting the empty keyword stays
        // correct — it is what gets past the driver — but a connection with no database
        // may still fail at the server, and PR-1.7's error surface is what the user
        // will see. Do not read -354 as a bad host or bad credentials.
        builder["Database"] = descriptor.Database ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(descriptor.UserName))
        {
            builder["Uid"] = descriptor.UserName;
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder["Pwd"] = password;
        }

        // NFR-9: locales must be explicit where the user has stated them, otherwise
        // the client default silently decides collation and code-set behaviour.
        if (!string.IsNullOrWhiteSpace(descriptor.DatabaseLocale))
        {
            builder["DB_LOCALE"] = descriptor.DatabaseLocale;
        }

        if (!string.IsNullOrWhiteSpace(descriptor.ClientLocale))
        {
            builder["CLIENT_LOCALE"] = descriptor.ClientLocale;
        }

        if (descriptor.ConnectTimeoutSeconds > 0)
        {
            // "Connection Timeout" is handled by System.Data.Odbc itself, which maps
            // it to SQL_ATTR_LOGIN_TIMEOUT. CONNECT_TIMEOUT was tried first and is
            // not a driver keyword at all — the driver ignores keywords it does not
            // know, so the timeout was silently never applied.
            builder["Connection Timeout"] = descriptor.ConnectTimeoutSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // PR-1.10 (encrypted connections) is deliberately NOT attempted here. The
        // driver ignores unrecognised keywords rather than rejecting them, so an
        // invented SECURITY=ssl would appear to work while doing nothing at all —
        // the worst possible outcome for a security feature, and exactly what
        // PR-8.4 warns about. Informix encryption is configured through the
        // sqlhosts CSM option on the server side; wiring that up is Slice 4 work
        // and needs a server configured for it to verify against.
        if (descriptor.UseEncryption)
        {
            throw new NotSupportedException(
                "Encrypted connections (PR-1.10) are not implemented. The CSDK ODBC driver "
                + "silently ignores unknown connection-string keywords, so IMS will not pretend "
                + "to enable encryption it cannot verify. Configure the CSM in sqlhosts instead.");
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// The same string with the password removed, safe to put in a log or an error.
    /// </summary>
    /// <remarks>
    /// PR-6.3. Redaction also happens at the logging boundary, but a connection
    /// string is the one value most likely to be interpolated into a message by
    /// hand, so it gets an explicit safe form as well.
    /// </remarks>
    public static string ForLogging(string connectionString) =>
        Core.Diagnostics.Redaction.ConnectionString(connectionString);
}
