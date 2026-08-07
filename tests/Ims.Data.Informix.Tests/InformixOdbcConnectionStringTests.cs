using FluentAssertions;
using Ims.Core.Connections;
using Xunit;

namespace Ims.Data.Informix.Tests;

public class InformixOdbcConnectionStringTests
{
    private const string DriverName = "IBM INFORMIX ODBC DRIVER (64-bit)";

    private static ConnectionDescriptor Descriptor(
        string? database = "sysmaster",
        string? user = "kaveh") => new()
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            DisplayName = "Development",
            ServerName = "ol_dev",
            Host = "192.0.2.10",
            Service = "9088",
            Protocol = "onsoctcp",
            Database = database,
            UserName = user,
        };

    [Fact]
    public void Carries_the_sqlhosts_quartet_individually()
    {
        // PR-1.1: a connection IMS holds is fully described by its own descriptor,
        // rather than depending on the machine's sqlhosts being correct.
        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor(), DriverName, password: "hunter2");

        connectionString.Should().Contain("Server=ol_dev");
        connectionString.Should().Contain("Host=192.0.2.10");
        connectionString.Should().Contain("Service=9088");
        connectionString.Should().Contain("Protocol=onsoctcp");
        connectionString.Should().Contain("Database=sysmaster");
    }

    [Fact]
    public void Uses_the_discovered_driver_name_verbatim()
    {
        // The name carries a bitness suffix that varies by SDK build, so it is
        // discovered by CsdkLocator rather than assumed.
        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor(), DriverName, password: null);

        connectionString.Should().Contain(DriverName);
    }

    [Fact]
    public void Always_emits_Database_even_when_there_is_none()
    {
        // Measured against the CSDK 4.10 driver: omitting the Database keyword makes
        // it fail with -11060 "General error" before any network I/O, which reads as
        // a connection problem and is not one. Present-but-empty gives a real
        // connection attempt. Connecting at instance level is legitimate, so the
        // keyword has to be there with an empty value.
        //
        // This is the bug the first smoke-test run against a real server found.
        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor(database: null, user: null), DriverName, password: null);

        connectionString.Should().Contain("Database=");
    }

    [Fact]
    public void Omits_credentials_that_were_not_supplied()
    {
        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor(database: null, user: null), DriverName, password: null);

        connectionString.Should().NotContain("Uid=");
        connectionString.Should().NotContain("Pwd=");
    }

    [Fact]
    public void Uses_the_timeout_keyword_the_ODBC_provider_actually_honours()
    {
        // CONNECT_TIMEOUT is not a driver keyword, and the driver ignores keywords
        // it does not recognise rather than rejecting them — so the original spelling
        // silently did nothing. "Connection Timeout" is handled by System.Data.Odbc.
        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor(), DriverName, password: null);

        connectionString.Should().Contain("Connection Timeout=15");
        connectionString.Should().NotContain("CONNECT_TIMEOUT");
    }

    [Fact]
    public void Refuses_to_pretend_it_can_encrypt()
    {
        // The driver ignores unknown keywords, so emitting SECURITY=ssl would look
        // like encryption while providing none. PR-8.4: a half-implemented
        // capability is worse than an absent one, and most of all for this one.
        ConnectionDescriptor descriptor = Descriptor() with { UseEncryption = true };

        Action act = () => InformixOdbcConnectionString.Build(descriptor, DriverName, null);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*not implemented*");
    }

    [Fact]
    public void Includes_the_locales_when_stated()
    {
        // NFR-9: otherwise the client default silently decides collation behaviour.
        ConnectionDescriptor descriptor = Descriptor() with
        {
            DatabaseLocale = "en_US.819",
            ClientLocale = "en_US.CP1252",
        };

        string connectionString = InformixOdbcConnectionString.Build(
            descriptor, DriverName, password: null);

        connectionString.Should().Contain("DB_LOCALE=en_US.819");
        connectionString.Should().Contain("CLIENT_LOCALE=en_US.CP1252");
    }

    [Fact]
    public void The_logging_form_carries_no_password()
    {
        // PR-6.3. A connection string is the value most likely to end up in an error
        // message by hand, so it has an explicit safe form as well as boundary redaction.
        string connectionString = InformixOdbcConnectionString.Build(
            Descriptor(), DriverName, password: "hunter2");

        connectionString.Should().Contain("hunter2", "the real string must still work");

        string safe = InformixOdbcConnectionString.ForLogging(connectionString);

        safe.Should().NotContain("hunter2");
        safe.Should().Contain("Server=ol_dev", "the diagnostic shape must survive");
    }

    [Fact]
    public void Rejects_a_missing_driver_name()
    {
        Action act = () => InformixOdbcConnectionString.Build(Descriptor(), "  ", null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_a_null_descriptor()
    {
        Action act = () => InformixOdbcConnectionString.Build(null!, DriverName, null);

        act.Should().Throw<ArgumentNullException>();
    }
}
