using Ims.Core.Connections;
using Ims.Core.Data;
using Microsoft.Extensions.Logging;

namespace Ims.Data.Informix;

/// <summary>
/// Creates ODBC-backed sessions, using the driver discovered at startup.
/// </summary>
/// <remarks>
/// The composition seam. Everything above this point is written against
/// <see cref="IInformixSessionFactory"/>, so replacing the provider — should the
/// smoke test show the ODBC route cannot meet PR-3.5 or PR-4.2 — means writing a
/// second factory and changing one registration.
/// </remarks>
public sealed class InformixOdbcSessionFactory : IInformixSessionFactory
{
    private readonly string _driverName;
    private readonly ILoggerFactory _loggerFactory;

    public InformixOdbcSessionFactory(CsdkDetectionResult csdk, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(csdk);

        if (!csdk.IsUsable || string.IsNullOrWhiteSpace(csdk.OdbcDriverName))
        {
            // PR-1.8 means we should never get here: the shell reports an unusable
            // SDK at startup rather than letting it surface as a connection failure.
            throw new InvalidOperationException(
                "The Informix Client SDK is not usable, so no session can be created. "
                + (csdk.Message ?? "No further detail."));
        }

        _driverName = csdk.OdbcDriverName;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IInformixSession Create(ConnectionDescriptor descriptor, ICredentialResolver credentials) =>
        new InformixOdbcSession(
            descriptor,
            credentials,
            _driverName,
            _loggerFactory.CreateLogger<InformixOdbcSession>());
}
