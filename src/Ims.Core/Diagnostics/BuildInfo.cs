using System.Reflection;

namespace Ims.Core.Diagnostics;

/// <summary>
/// Which build this is: the version, and the commit it was made from.
/// </summary>
/// <remarks>
/// <para>
/// A pilot user reporting a problem has to be able to say what they are running,
/// and a version number alone cannot do it — several builds share
/// <c>0.1.0</c>, and the folder they were given may have been copied from
/// anywhere. The commit id is what makes a report reproducible.
/// </para>
/// <para>
/// The value comes from <see cref="AssemblyInformationalVersionAttribute"/>, which
/// the <c>StampGitCommit</c> target in <c>Directory.Build.props</c> fills at build
/// time as <c>0.1.0+abc123def456</c>, with <c>+modified</c> appended when the tree
/// had uncommitted changes. That suffix is the important one: it says the build
/// matches no commit at all, so a bug report naming the id would send someone to
/// code that was never built.
/// </para>
/// </remarks>
public static class BuildInfo
{
    private const string Unknown = "unknown";

    static BuildInfo()
    {
        string? informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            Version = typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? Unknown;
            Commit = Unknown;
            return;
        }

        // "0.1.0+abc123def456" or "0.1.0+abc123def456+modified". Split on the first
        // '+' only: everything after it describes the source, not the version.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);

        if (plus < 0)
        {
            Version = informational;
            Commit = Unknown;
            return;
        }

        Version = informational[..plus];

        string rest = informational[(plus + 1)..];
        int modified = rest.IndexOf("+modified", StringComparison.Ordinal);

        if (modified >= 0)
        {
            Commit = rest[..modified];
            IsModified = true;
        }
        else
        {
            Commit = rest;
        }

        if (string.IsNullOrWhiteSpace(Commit))
        {
            Commit = Unknown;
        }
    }

    /// <summary>The product version, e.g. <c>0.1.0</c>.</summary>
    public static string Version { get; }

    /// <summary>The abbreviated commit this was built from, or <c>unknown</c>.</summary>
    public static string Commit { get; }

    /// <summary>
    /// True when the working tree had uncommitted changes at build time.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than hiding: it means <see cref="Commit"/> does not
    /// describe what is actually running.
    /// </remarks>
    public static bool IsModified { get; }

    /// <summary>One line naming this build, for an About box or a bug report.</summary>
    public static string Describe() =>
        Commit == Unknown
            ? $"{Version} (commit unknown)"
            : $"{Version} ({Commit}{(IsModified ? ", modified" : string.Empty)})";
}
