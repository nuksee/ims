using Microsoft.Win32;

namespace Ims.Data.Informix;

/// <summary>Why the Client SDK could not be used.</summary>
public enum CsdkProblem
{
    None = 0,

    /// <summary>No INFORMIXDIR in the registry or the environment.</summary>
    NotInstalled,

    /// <summary>INFORMIXDIR is set but the directory is not there.</summary>
    DirectoryMissing,

    /// <summary>INFORMIXDIR exists but the client libraries are not under it.</summary>
    LibrariesMissing,

    /// <summary>The SDK is present but no Informix ODBC driver is registered.</summary>
    OdbcDriverNotRegistered,

    /// <summary>A driver is registered but its DLL is not on disk.</summary>
    OdbcDriverFileMissing,

    /// <summary>Only a 32-bit driver is registered, and IMS is a 64-bit process.</summary>
    OdbcDriverBitnessMismatch,
}

/// <summary>The outcome of looking for the Client SDK.</summary>
public sealed record CsdkDetectionResult
{
    public required bool IsUsable { get; init; }

    public CsdkProblem Problem { get; init; }

    /// <summary>Plain-language description of what is wrong, or null when nothing is.</summary>
    public string? Message { get; init; }

    /// <summary>What the user should do about it. Null when there is nothing to do.</summary>
    public string? Remedy { get; init; }

    public string? InformixDir { get; init; }

    /// <summary>The registered ODBC driver name, used verbatim in the connection string.</summary>
    public string? OdbcDriverName { get; init; }

    public string? OdbcDriverPath { get; init; }

    /// <summary>The SDK version string, where it could be read.</summary>
    public string? Version { get; init; }
}

/// <summary>
/// Finds the Informix Client SDK and the ODBC driver IMS connects through.
/// </summary>
/// <remarks>
/// <para>
/// PR-1.8: "Report a missing or misconfigured Client SDK clearly at startup, not
/// as a connection failure." That distinction is the whole point of this class.
/// A user whose CSDK is absent should be told the SDK is absent — not handed a
/// connection timeout to misdiagnose. NFR-6 makes the SDK a prerequisite and
/// DEC-10 forbids bundling it, so its absence is a case IMS must handle well
/// rather than an edge case.
/// </para>
/// <para>
/// Detection is deliberately layered: INFORMIXDIR, then the libraries under it,
/// then the ODBC registration, then the driver file. Each layer gives a different
/// and more useful message than "could not connect".
/// </para>
/// </remarks>
public static class CsdkLocator
{
    private const string InformixEnvironmentKey = @"SOFTWARE\Informix\Environment";
    private const string OdbcInstKey = @"SOFTWARE\ODBC\ODBCINST.INI";

    /// <summary>The client library that must be present for the ODBC path to work.</summary>
    private const string ClientLibrary = "iclit09b.dll";

    /// <summary>
    /// Looks for the SDK. Pure inspection of the registry and the file system —
    /// touches no network and opens no connection.
    /// </summary>
    public static CsdkDetectionResult Detect()
    {
        string? informixDir = ReadInformixDir();

        if (string.IsNullOrWhiteSpace(informixDir))
        {
            return new CsdkDetectionResult
            {
                IsUsable = false,
                Problem = CsdkProblem.NotInstalled,
                Message = "The IBM Informix Client SDK was not found. IMS could not read "
                          + @"INFORMIXDIR from HKEY_LOCAL_MACHINE\SOFTWARE\Informix\Environment "
                          + "or from the environment.",
                Remedy = "Install the Informix Client SDK. IMS requires it and does not bundle it.",
            };
        }

        informixDir = informixDir.TrimEnd('\\', '/');

        if (!Directory.Exists(informixDir))
        {
            return new CsdkDetectionResult
            {
                IsUsable = false,
                Problem = CsdkProblem.DirectoryMissing,
                InformixDir = informixDir,
                Message = $"INFORMIXDIR points at '{informixDir}', but that directory does not exist.",
                Remedy = "Repair or reinstall the Informix Client SDK, or correct INFORMIXDIR.",
            };
        }

        string libraryPath = Path.Combine(informixDir, "bin", ClientLibrary);
        if (!File.Exists(libraryPath))
        {
            return new CsdkDetectionResult
            {
                IsUsable = false,
                Problem = CsdkProblem.LibrariesMissing,
                InformixDir = informixDir,
                Message = $"'{informixDir}' does not contain the client libraries "
                          + $"(expected {ClientLibrary} under bin).",
                Remedy = "Repair or reinstall the Informix Client SDK.",
            };
        }

        string? version = ReadVersion(libraryPath);

        OdbcDriverRegistration? driver = FindOdbcDriver();

        if (driver is null)
        {
            return new CsdkDetectionResult
            {
                IsUsable = false,
                Problem = CsdkProblem.OdbcDriverNotRegistered,
                InformixDir = informixDir,
                Version = version,
                Message = "The Informix Client SDK is installed, but no Informix ODBC driver is "
                          + "registered for 64-bit applications.",
                Remedy = "Re-run the Client SDK installer and include the ODBC driver, or register "
                         + "it with the 64-bit ODBC Data Source Administrator.",
            };
        }

        if (!File.Exists(driver.DriverPath))
        {
            return new CsdkDetectionResult
            {
                IsUsable = false,
                Problem = CsdkProblem.OdbcDriverFileMissing,
                InformixDir = informixDir,
                Version = version,
                OdbcDriverName = driver.Name,
                OdbcDriverPath = driver.DriverPath,
                Message = $"The ODBC driver '{driver.Name}' is registered, but its library "
                          + $"'{driver.DriverPath}' is missing.",
                Remedy = "Repair or reinstall the Informix Client SDK.",
            };
        }

        return new CsdkDetectionResult
        {
            IsUsable = true,
            Problem = CsdkProblem.None,
            InformixDir = informixDir,
            Version = version,
            OdbcDriverName = driver.Name,
            OdbcDriverPath = driver.DriverPath,
        };
    }

    /// <summary>
    /// INFORMIXDIR from the registry, falling back to the process environment.
    /// </summary>
    /// <remarks>
    /// The registry is checked first because the CSDK installer writes it there and
    /// a machine can have the SDK installed without the variable ever being set in
    /// a user's environment — which is the case on the development workstation.
    /// </remarks>
    public static string? ReadInformixDir()
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(InformixEnvironmentKey);
            if (key?.GetValue("INFORMIXDIR") is string fromRegistry
                && !string.IsNullOrWhiteSpace(fromRegistry))
            {
                return fromRegistry;
            }
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("INFORMIXDIR");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    /// <summary>
    /// Finds a registered Informix ODBC driver in the 64-bit registry view.
    /// </summary>
    /// <remarks>
    /// The name is discovered rather than hard-coded, because it carries a bitness
    /// suffix that varies by SDK build — on this workstation it is
    /// "IBM INFORMIX ODBC DRIVER (64-bit)". It goes into the connection string
    /// verbatim, so guessing it is not an option.
    /// </remarks>
    public static OdbcDriverRegistration? FindOdbcDriver()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using RegistryKey? odbcInst = Registry.LocalMachine.OpenSubKey(OdbcInstKey);
        if (odbcInst is null)
        {
            return null;
        }

        // Prefer an explicitly 64-bit driver, then any Informix driver at all.
        OdbcDriverRegistration? fallback = null;

        foreach (string name in odbcInst.GetSubKeyNames())
        {
            if (!name.Contains("INFORMIX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using RegistryKey? driverKey = odbcInst.OpenSubKey(name);
            if (driverKey?.GetValue("Driver") is not string driverPath
                || string.IsNullOrWhiteSpace(driverPath))
            {
                continue;
            }

            var registration = new OdbcDriverRegistration(name, driverPath);

            if (name.Contains("64", StringComparison.Ordinal))
            {
                return registration;
            }

            fallback ??= registration;
        }

        return fallback;
    }

    private static string? ReadVersion(string libraryPath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(libraryPath);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion.Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A registered ODBC driver: the name to use, and the library behind it.</summary>
public sealed record OdbcDriverRegistration(string Name, string DriverPath);
