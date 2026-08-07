namespace Ims.Core.Data;

/// <summary>
/// What IMS knows about the instance it is connected to.
/// </summary>
/// <remarks>
/// <para>
/// NFR-4 and RSK-9 both say the same thing in different words: <em>detect capabilities
/// rather than branching on version number</em>, so that another version later costs
/// little. This type is where that discipline lives — call sites ask
/// <see cref="Supports"/>, not "is this 14.10?".
/// </para>
/// <para>
/// That discipline became load-bearing on 2026-08-06, when DEC-5 narrowed v1 to 14.10
/// and descoped 12.10. 12.10 is untested, not refused: because nothing branches on the
/// version number, a 12.10 server should degrade on an absent catalogue feature rather
/// than fail. Introducing a version comparison here would turn that soft landing into a
/// hard one.
/// </para>
/// <para>
/// The version is still recorded, because PR-5.6 wants to show it and because a
/// human diagnosing a problem needs it. It is just not what the code branches on.
/// </para>
/// </remarks>
public sealed record InformixServerInfo
{
    /// <summary>The raw banner, shown verbatim on request (PR-8.2).</summary>
    public required string VersionBanner { get; init; }

    public required Version Version { get; init; }

    /// <summary>The INFORMIXSERVER name the instance reports for itself.</summary>
    public string? ServerName { get; init; }

    /// <summary>Capabilities probed at connect time, keyed by <see cref="InformixCapability"/>.</summary>
    public required IReadOnlySet<InformixCapability> Capabilities { get; init; }

    public bool Supports(InformixCapability capability) => Capabilities.Contains(capability);
}

/// <summary>
/// A capability IMS probes for rather than assuming from a version number (NFR-4).
/// </summary>
public enum InformixCapability
{
    /// <summary>The <c>sysmaster</c> database is readable by this user. Gates Slice 3 (Q-1, AS-3).</summary>
    SysMasterReadable,

    /// <summary>Session-level lock and wait detail is visible (PR-5.2, PR-5.3).</summary>
    SessionLockDetail,

    /// <summary>Fragmentation strategy is retrievable from the catalogue (PR-2.4).</summary>
    FragmentationMetadata,

    /// <summary>Statistics currency can be determined (PR-2.5).</summary>
    StatisticsCurrency,

    /// <summary>The connection is encrypted (PR-1.10).</summary>
    EncryptedConnection,

    /// <summary>Smart large object access is available (PR-4.5).</summary>
    SmartLargeObjects,
}
