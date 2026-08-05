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

        if (!string.IsNullOrWhiteSpace(descriptor.Database))
        {
            builder["Database"] = descriptor.Database;
        }

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
            builder["CONNECT_TIMEOUT"] = descriptor.ConnectTimeoutSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (descriptor.UseEncryption)
        {
            // PR-1.10 is a Should, scheduled for Slice 4. The keyword below is the
            // documented CSDK form but has NOT been verified against a server that
            // has encryption configured — Ims.SmokeTest is where that gets settled.
            builder["SECURITY"] = "ssl";
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
