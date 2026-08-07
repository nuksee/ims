using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// Enforces PR-6.5 ("Emit no telemetry") and the DEC-10 non-redistribution constraint.
/// </summary>
/// <remarks>
/// <para>
/// A promise nobody checks is a promise that quietly stops being true. PR-6.5 is
/// exactly the kind of thing that gets broken by a transitive dependency nobody
/// looked at, so it is checked mechanically here rather than trusted.
/// </para>
/// </remarks>
public class DependencyPolicyTests
{
    /// <summary>
    /// Package name fragments that indicate analytics, crash reporting or telemetry.
    /// </summary>
    private static readonly string[] TelemetryMarkers =
    [
        "ApplicationInsights",
        "OpenTelemetry",
        "AppCenter",
        "Sentry",
        "NewRelic",
        "Datadog",
        "Segment",
        "Mixpanel",
        "GoogleAnalytics",
        "Bugsnag",
        "Raygun",
    ];

    /// <summary>
    /// IBM client packages IMS must not take a dependency on: DEC-10 requires a
    /// separately installed CSDK rather than redistributed libraries.
    /// </summary>
    private static readonly string[] RedistributionMarkers =
    [
        "IBM.Data",
        "Net.IBM.Data",
        "IBM.EntityFrameworkCore",
    ];

    [Fact]
    public void No_telemetry_package_is_declared()
    {
        IReadOnlyList<string> packages = DeclaredPackages();

        packages.Should().NotBeEmpty("the central package list should have been found");

        string[] offenders = packages
            .Where(p => TelemetryMarkers.Any(m => p.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        offenders.Should().BeEmpty(
            "PR-6.5 says IMS emits no telemetry, and these packages exist to send data somewhere");
    }

    [Fact]
    public void No_IBM_client_library_is_redistributed()
    {
        IReadOnlyList<string> packages = DeclaredPackages();

        string[] offenders = packages
            .Where(p => RedistributionMarkers.Any(m => p.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        offenders.Should().BeEmpty(
            "DEC-10 keeps a product or open-source release possible, which means IMS requires a "
            + "separately installed Client SDK rather than bundling IBM libraries");
    }

    [Fact]
    public void Every_package_version_is_centrally_managed()
    {
        // One reviewable list is what makes the two tests above meaningful.
        DirectoryInfo root = RepositoryRoot();

        string[] withInlineVersions = Directory
            .EnumerateFiles(root.FullName, "*.csproj", SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path)
                .Descendants("PackageReference")
                .Any(e => e.Attribute("Version") is not null))
            .Select(Path.GetFileName)
            .ToArray()!;

        withInlineVersions.Should().BeEmpty(
            "package versions belong in Directory.Packages.props");
    }

    private static string[] DeclaredPackages()
    {
        string path = Path.Combine(RepositoryRoot().FullName, "Directory.Packages.props");

        return XDocument.Load(path)
            .Descendants("PackageVersion")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray();
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return directory
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
