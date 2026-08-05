using FluentAssertions;
using Xunit;

namespace Ims.Data.Informix.Tests;

/// <summary>
/// Detection depends on machine state, so these assert that the result is
/// internally coherent rather than that the SDK is present. The development
/// workstation has CSDK 4.10; CI does not, and both must pass.
/// </summary>
public class CsdkLocatorTests
{
    [Fact]
    public void Detection_never_throws()
    {
        // PR-1.8 exists so that a misconfigured SDK is reported clearly. A detector
        // that throws would produce exactly the opaque startup failure it prevents.
        Action act = () => CsdkLocator.Detect();

        act.Should().NotThrow();
    }

    [Fact]
    public void A_usable_result_names_the_driver_it_found()
    {
        CsdkDetectionResult result = CsdkLocator.Detect();

        if (!result.IsUsable)
        {
            return; // covered by the failure-shape test below
        }

        result.Problem.Should().Be(CsdkProblem.None);
        result.InformixDir.Should().NotBeNullOrWhiteSpace();
        result.OdbcDriverName.Should().NotBeNullOrWhiteSpace(
            "the driver name goes into the connection string verbatim");
        result.OdbcDriverPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(result.OdbcDriverPath).Should().BeTrue();
        result.Message.Should().BeNull();
    }

    [Fact]
    public void An_unusable_result_says_what_is_wrong_and_what_to_do()
    {
        CsdkDetectionResult result = CsdkLocator.Detect();

        if (result.IsUsable)
        {
            return;
        }

        result.Problem.Should().NotBe(CsdkProblem.None);
        result.Message.Should().NotBeNullOrWhiteSpace(
            "PR-1.8 requires this to be reported clearly at startup");
        result.Remedy.Should().NotBeNullOrWhiteSpace(
            "NFR-6 makes the SDK a prerequisite, so the user needs to be told how to supply it");
    }

    [Fact]
    public void Reading_INFORMIXDIR_never_throws()
    {
        Action act = () => CsdkLocator.ReadInformixDir();

        act.Should().NotThrow();
    }

    [Fact]
    public void Driver_discovery_never_throws()
    {
        Action act = () => CsdkLocator.FindOdbcDriver();

        act.Should().NotThrow();
    }
}
