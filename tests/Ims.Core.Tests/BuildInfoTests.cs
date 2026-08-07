using FluentAssertions;
using Ims.Core.Diagnostics;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// The build stamp a bug report is identified by.
/// </summary>
/// <remarks>
/// These run against the test assembly's own stamp, which the same
/// <c>StampGitCommit</c> target in Directory.Build.props produces — so they check
/// the build actually wired it up, not just that the parser can parse.
/// </remarks>
public class BuildInfoTests
{
    [Fact]
    public void The_version_is_reported()
    {
        BuildInfo.Version.Should().NotBeNullOrWhiteSpace();
        BuildInfo.Version.Should().NotContain("+", "the commit belongs in Commit, not Version");
    }

    /// <summary>
    /// The version in the build must match the tag pointing at this commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// v0.1.1-pilot was tagged while Directory.Build.props still said 0.1.0, so the
    /// published build's About window contradicted the tag it was cut from. The
    /// commit id cannot drift — it is read from git at build time — but the version
    /// is hand-maintained, so it is the half that needs checking.
    /// </para>
    /// <para>
    /// Silent when HEAD carries no tag, which is the normal state between releases:
    /// this exists to catch a tag that disagrees, not to demand one.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_version_matches_any_tag_on_this_commit()
    {
        string? tag = TryGetExactTag();

        if (tag is null)
        {
            return;
        }

        // "v0.1.1-pilot" -> "0.1.1"
        string expected = tag.TrimStart('v', 'V').Split('-')[0];

        BuildInfo.Version.Should().Be(
            expected,
            $"this commit is tagged {tag}, and a build that reports a different "
            + "version makes the tag useless for saying what someone is running");
    }

    private static string? TryGetExactTag()
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "describe --tags --exact-match",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            if (git is null)
            {
                return null;
            }

            string output = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit(5000);

            // Non-zero simply means "no tag here", which is not a failure.
            return git.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git on the machine — a source drop can still run the tests.
            return null;
        }
    }

    [Fact]
    public void The_commit_is_stamped_by_the_build()
    {
        // "unknown" is the honest answer from a source drop with no .git, and the
        // property is allowed to say it — but a normal build here has a repository,
        // so this failing means the MSBuild target stopped running.
        BuildInfo.Commit.Should().NotBeNullOrWhiteSpace();
        BuildInfo.Commit.Should().NotContain(
            "+", "the modified marker is parsed out into IsModified");
    }

    [Fact]
    public void The_commit_looks_like_an_abbreviated_sha_or_says_it_does_not_know()
    {
        if (BuildInfo.Commit == "unknown")
        {
            return;
        }

        BuildInfo.Commit.Should().MatchRegex(
            "^[0-9a-f]{7,40}$",
            "a bug report quotes this back, so it has to be a commit id and nothing else");
    }

    [Fact]
    public void Describe_names_the_build_in_one_line()
    {
        string described = BuildInfo.Describe();

        described.Should().Contain(BuildInfo.Version);
        described.Should().Contain(BuildInfo.Commit);

        if (BuildInfo.IsModified)
        {
            described.Should().Contain(
                "modified",
                "a build from a dirty tree matches no commit, and hiding that would "
                + "send someone reading a bug report to code that was never built");
        }
    }
}
