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
