using FluentAssertions;
using Xunit;

namespace Ims.Data.Informix.Tests;

public class SqlHostsReaderTests
{
    [Fact]
    public void Parses_the_four_mandatory_fields()
    {
        // The shape found in %INFORMIXDIR%\etc\sqlhosts on the development workstation.
        var entries = SqlHostsReader.Parse(["demo_srv  onsoctcp  192.0.2.10  9088"]);

        entries.Should().ContainSingle();
        entries[0].ServerName.Should().Be("demo_srv");
        entries[0].Protocol.Should().Be("onsoctcp");
        entries[0].Host.Should().Be("192.0.2.10");
        entries[0].Service.Should().Be("9088");
        entries[0].Options.Should().BeNull();
        entries[0].Source.Should().Be(SqlHostsSource.File);
    }

    [Fact]
    public void Keeps_the_options_field_verbatim()
    {
        // PR-8.2: IMS does not interpret the options field, so it shows it unaltered.
        var entries = SqlHostsReader.Parse(
            ["ol_prod onsoctcp host1 9088 s=2,b=32767,csm=(SSL)"]);

        entries[0].Options.Should().Be("s=2,b=32767,csm=(SSL)");
    }

    [Fact]
    public void Ignores_comments_and_blank_lines()
    {
        var entries = SqlHostsReader.Parse(
        [
            "# production",
            string.Empty,
            "   ",
            "ol_prod onsoctcp host1 9088",
            "ol_uat onsoctcp host2 9089  # the UAT box",
        ]);

        entries.Should().HaveCount(2);
        entries[1].ServerName.Should().Be("ol_uat");
        entries[1].Options.Should().BeNull("the trailing comment is not an options field");
    }

    [Fact]
    public void Skips_lines_that_are_missing_a_mandatory_field()
    {
        var entries = SqlHostsReader.Parse(["ol_broken onsoctcp host1"]);

        entries.Should().BeEmpty();
    }

    [Fact]
    public void Tolerates_tabs_and_irregular_spacing()
    {
        var entries = SqlHostsReader.Parse(["ol_prod\tonsoctcp\t host1 \t9088"]);

        entries.Should().ContainSingle();
        entries[0].Host.Should().Be("host1");
    }

    [Fact]
    public void Returns_nothing_for_a_file_that_is_not_there()
    {
        SqlHostsReader
            .ReadFromFile(Path.Combine(Path.GetTempPath(), "ims-no-such-sqlhosts"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Reads_a_real_file_from_disk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ims-sqlhosts-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllLines(path, ["# test", "ol_test onsoctcp localhost 9088"]);

            var entries = SqlHostsReader.ReadFromFile(path);

            entries.Should().ContainSingle();
            entries[0].ServerName.Should().Be("ol_test");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_a_null_sequence()
    {
        Action act = () => SqlHostsReader.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Reading_the_machine_never_throws()
    {
        // Runs on a workstation that has entries and in CI, which has none. Either is
        // a valid outcome; failing is not.
        Action act = () => SqlHostsReader.ReadAll();

        act.Should().NotThrow();
    }
}
