namespace Ims.Core.Connections;

/// <summary>
/// Which estate a connection points at.
/// </summary>
/// <remarks>
/// PR-1.5 requires this to be shown persistently and unmistakably, and NFR-8
/// forbids relying on colour alone to convey it — so this is a first-class part
/// of the connection identity rather than a UI decoration.
/// </remarks>
public enum InformixEnvironment
{
    /// <summary>Environment not stated. Treated as the most cautious case in the UI.</summary>
    Unspecified = 0,
    Development = 1,
    Uat = 2,
    Production = 3,
}

/// <summary>
/// How the user authenticates to the instance. Selected per connection (DEC-6, PR-1.3).
/// </summary>
public enum InformixAuthenticationMode
{
    /// <summary>Informix local operating-system or database authentication.</summary>
    Local = 0,

    /// <summary>LDAP or PAM-backed authentication, as configured on the server.</summary>
    LdapPam = 1,
}

/// <summary>
/// Everything needed to reach one Informix instance, minus the secret.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately carries no password. Credentials live only in Windows Credential
/// Manager (DEC-9, PR-1.4) and are fetched at connect time, so a descriptor is
/// safe to serialise into the saved instance list and safe to put in a log line.
/// </para>
/// <para>
/// The <see cref="ServerName"/> / <see cref="Host"/> / <see cref="Service"/> /
/// <see cref="Protocol"/> quartet mirrors <c>sqlhosts</c> semantics exactly
/// (PR-1.1), which is what lets PR-1.9 import an existing file without a
/// lossy translation step.
/// </para>
/// </remarks>
public sealed record ConnectionDescriptor
{
    /// <summary>Stable identity for this saved connection. Also the Credential Manager key.</summary>
    public required Guid Id { get; init; }

    /// <summary>Display name in the instance list (PR-1.2).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The Informix server (INFORMIXSERVER) name, as it appears in <c>sqlhosts</c>.</summary>
    public required string ServerName { get; init; }

    /// <summary>Hostname or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>Service name or port number.</summary>
    public required string Service { get; init; }

    /// <summary>Network protocol, e.g. <c>onsoctcp</c>.</summary>
    public string Protocol { get; init; } = "onsoctcp";

    /// <summary>Database to open on connect. May be null to connect at instance level.</summary>
    public string? Database { get; init; }

    /// <summary>Username. The matching secret is retrieved from Credential Manager by <see cref="Id"/>.</summary>
    public string? UserName { get; init; }

    public InformixAuthenticationMode AuthenticationMode { get; init; } = InformixAuthenticationMode.Local;

    /// <summary>PR-1.5. Never inferred from the server name — the user states it.</summary>
    public InformixEnvironment Environment { get; init; } = InformixEnvironment.Unspecified;

    /// <summary>Optional grouping label for the instance list (PR-1.2).</summary>
    public string? Group { get; init; }

    /// <summary>NFR-9. Null means "let the server decide".</summary>
    public string? DatabaseLocale { get; init; }

    /// <summary>NFR-9. Null means "let the client default apply".</summary>
    public string? ClientLocale { get; init; }

    /// <summary>PR-1.10. Requested only where the server is configured for it.</summary>
    public bool UseEncryption { get; init; }

    /// <summary>Seconds to wait for a connection before giving up.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// True when this connection needs the extra care PR-1.5 exists to prompt.
    /// </summary>
    public bool IsProduction => Environment == InformixEnvironment.Production;

    /// <summary>
    /// A short, unambiguous label for the instance an editor or pane is targeting (PR-1.6).
    /// </summary>
    public string TargetLabel =>
        string.IsNullOrWhiteSpace(Database)
            ? $"{DisplayName} ({ServerName})"
            : $"{DisplayName} ({ServerName}/{Database})";
}
