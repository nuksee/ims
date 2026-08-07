using FluentAssertions;
using Xunit;

namespace Ims.Data.Informix.Tests;

/// <summary>
/// Covers every PR-1.8 failure branch.
/// </summary>
/// <remarks>
/// These matter more than the happy path. A missing SDK is reported once, at
/// startup, to a user who may not know what a Client SDK is — and it is the one
/// path that cannot be exercised on a workstation where the SDK is installed
/// correctly. Hence <c>Evaluate</c> taking its inputs as parameters.
/// </remarks>
public class CsdkLocatorEvaluateTests
{
    private const string Dir = @"C:\Program Files\IBM Informix Client SDK";
    private const string LibPath = @"C:\Program Files\IBM Informix Client SDK\bin\iclit09b.dll";
    private const string DriverPath = @"C:\Program Files\IBM Informix Client SDK\bin\iclit09b.dll";

    private static readonly OdbcDriverRegistration Driver64 =
        new("IBM INFORMIX ODBC DRIVER (64-bit)", DriverPath);

    private static readonly OdbcDriverRegistration Driver32 =
        new("IBM INFORMIX ODBC DRIVER", DriverPath, Is64Bit: false);

    /// <param name="omitDriver">
    /// True to model "no ODBC driver registered". A null <paramref name="driver"/>
    /// cannot express that, because null means "use the default" here.
    /// </param>
    private static CsdkDetectionResult Evaluate(
        string? informixDir = Dir,
        OdbcDriverRegistration? driver = null,
        bool omitDriver = false,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null,
        string? version = "4.10.FC1DE") =>
        CsdkLocator.Evaluate(
            informixDir,
            omitDriver ? null : driver ?? Driver64,
            directoryExists ?? (_ => true),
            fileExists ?? (_ => true),
            _ => version);

    [Fact]
    public void Reports_a_usable_SDK_with_the_driver_name_to_use()
    {
        CsdkDetectionResult result = Evaluate();

        result.IsUsable.Should().BeTrue();
        result.Problem.Should().Be(CsdkProblem.None);
        result.InformixDir.Should().Be(Dir);
        result.Version.Should().Be("4.10.FC1DE");
        result.OdbcDriverName.Should().Be("IBM INFORMIX ODBC DRIVER (64-bit)");
        result.Message.Should().BeNull();
        result.Remedy.Should().BeNull();
    }

    [Fact]
    public void Trims_a_trailing_separator_from_INFORMIXDIR()
    {
        // The registry value on the development workstation ends with a backslash.
        Evaluate(informixDir: Dir + @"\").InformixDir.Should().Be(Dir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reports_a_missing_SDK_when_INFORMIXDIR_is_unset(string? informixDir)
    {
        CsdkDetectionResult result = Evaluate(informixDir: informixDir);

        result.IsUsable.Should().BeFalse();
        result.Problem.Should().Be(CsdkProblem.NotInstalled);
        result.Message.Should().Contain("INFORMIXDIR");
        result.Remedy.Should().Contain("does not bundle it", "DEC-10 forbids redistribution");
    }

    [Fact]
    public void Reports_a_dangling_INFORMIXDIR_distinctly_from_an_absent_one()
    {
        CsdkDetectionResult result = Evaluate(directoryExists: _ => false);

        result.Problem.Should().Be(CsdkProblem.DirectoryMissing);
        result.Message.Should().Contain(Dir);
        result.InformixDir.Should().Be(Dir);
    }

    [Fact]
    public void Reports_an_INFORMIXDIR_that_lacks_the_client_libraries()
    {
        CsdkDetectionResult result = Evaluate(fileExists: path => path != LibPath);

        result.Problem.Should().Be(CsdkProblem.LibrariesMissing);
        result.Message.Should().Contain("iclit09b.dll");
    }

    [Fact]
    public void Reports_an_SDK_with_no_ODBC_driver_registered()
    {
        CsdkDetectionResult result = Evaluate(omitDriver: true);

        result.Problem.Should().Be(CsdkProblem.OdbcDriverNotRegistered);
        result.Version.Should().Be("4.10.FC1DE", "the SDK itself was found, so say so");
        result.Remedy.Should().Contain("ODBC");
    }

    [Fact]
    public void Reports_a_32_bit_only_driver_as_a_bitness_mismatch()
    {
        // The specific diagnosis matters: "not registered" would send the user
        // looking for something that is, in fact, right there.
        CsdkDetectionResult result = Evaluate(driver: Driver32);

        result.Problem.Should().Be(CsdkProblem.OdbcDriverBitnessMismatch);
        result.Message.Should().Contain("32-bit");
        result.Message.Should().Contain("64-bit");
        result.Remedy.Should().Contain("odbcad32.exe");
    }

    [Fact]
    public void Reports_a_registered_driver_whose_library_is_gone()
    {
        // On a real install the client library and the driver library are the same
        // file, so the driver is pointed elsewhere to isolate this branch.
        CsdkDetectionResult result = Evaluate(
            driver: new OdbcDriverRegistration(
                "IBM INFORMIX ODBC DRIVER (64-bit)",
                @"C:\gone\ifxodbc.dll"),
            fileExists: path => path == LibPath);

        result.Problem.Should().Be(CsdkProblem.OdbcDriverFileMissing);
        result.Message.Should().Contain(@"C:\gone\ifxodbc.dll");
    }

    [Fact]
    public void Every_failure_states_both_what_is_wrong_and_what_to_do()
    {
        // PR-1.8 is about the user understanding the problem, not just IMS detecting it.
        CsdkDetectionResult[] failures =
        [
            Evaluate(informixDir: null),
            Evaluate(directoryExists: _ => false),
            Evaluate(fileExists: path => path != LibPath),
            Evaluate(omitDriver: true),
            Evaluate(driver: Driver32),
            CsdkLocator.Evaluate(
                Dir,
                new OdbcDriverRegistration("d", @"C:\gone.dll"),
                _ => true,
                path => path == LibPath,
                _ => null),
        ];

        foreach (CsdkDetectionResult failure in failures)
        {
            failure.IsUsable.Should().BeFalse();
            failure.Problem.Should().NotBe(CsdkProblem.None);
            failure.Message.Should().NotBeNullOrWhiteSpace();
            failure.Remedy.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void A_version_that_cannot_be_read_is_not_fatal()
    {
        CsdkDetectionResult result = Evaluate(version: null);

        result.IsUsable.Should().BeTrue("an unreadable version string does not stop IMS working");
        result.Version.Should().BeNull();
    }
}
