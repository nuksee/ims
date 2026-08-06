using FluentAssertions;
using Ims.Core.Diagnostics;
using Xunit;

namespace Ims.Core.Tests;

public class RedactionTests
{
    [Theory]
    [InlineData("Driver={x};Uid=kaveh;Pwd=hunter2;", "hunter2")]
    [InlineData("Driver={x};UID=kaveh;PWD=hunter2;", "hunter2")]
    [InlineData("password = s3cret; host=a", "s3cret")]
    [InlineData("TOKEN=abc123", "abc123")]
    public void Strips_the_secret_from_a_connection_string(string input, string secret)
    {
        string redacted = Redaction.ConnectionString(input);

        redacted.Should().NotContain(secret);
        redacted.Should().Contain(Redaction.Marker);
    }

    [Fact]
    public void Redacts_a_password_that_contains_spaces()
    {
        // Found during a real smoke-test run: the value pattern stopped at the first
        // space, so everything after it survived into the output. A connection-string
        // value may contain spaces, and a password certainly may.
        string redacted = Redaction.ConnectionString(
            "Driver={x};Uid=kaveh;Pwd=correct horse battery staple;Database=testdb;");

        redacted.Should().NotContain("horse");
        redacted.Should().NotContain("battery");
        redacted.Should().NotContain("staple");
        redacted.Should().Contain("Database=testdb", "redaction must stop at the separator");
    }

    [Fact]
    public void Redacts_to_the_end_of_line_when_there_is_no_separator()
    {
        Redaction.Message("connect failed for Pwd=a long secret value")
            .Should().NotContain("secret");
    }

    [Fact]
    public void Keeps_the_non_secret_part_of_a_connection_string()
    {
        // NFR-10 still wants logs useful for debugging, so the diagnostic shape survives.
        string redacted = Redaction.ConnectionString(
            "Driver={IBM INFORMIX ODBC DRIVER (64-bit)};Server=ol_x;Uid=kaveh;Pwd=hunter2;");

        redacted.Should().Contain("Server=ol_x");
        redacted.Should().Contain("Uid=kaveh");
        redacted.Should().NotContain("hunter2");
    }

    [Fact]
    public void Removes_literals_from_a_statement_but_keeps_its_shape()
    {
        // PR-6.3: a literal can be a patient identifier or a password being set.
        string redacted = Redaction.Sql(
            "SELECT * FROM patient WHERE health_card = '1234567890' AND age > 65");

        redacted.Should().NotContain("1234567890");
        redacted.Should().NotContain("65");
        redacted.Should().Contain("SELECT");
        redacted.Should().Contain("patient");
        redacted.Should().Contain("health_card");
    }

    [Fact]
    public void Handles_Informix_doubled_quote_escaping()
    {
        string redacted = Redaction.Sql("SELECT * FROM t WHERE name = 'O''Brien'");

        redacted.Should().NotContain("Brien");
    }

    [Fact]
    public void Collapses_whitespace_and_truncates_long_statements()
    {
        string sql = "SELECT " + string.Join(", ", Enumerable.Range(0, 200).Select(i => $"column_{i}"));

        string redacted = Redaction.Sql(sql, maxLength: 64);

        redacted.Should().EndWith("[truncated]");
        redacted.Length.Should().BeLessThan(sql.Length);
    }

    [Fact]
    public void Does_not_mangle_identifiers_that_contain_digits()
    {
        string redacted = Redaction.Sql("SELECT col1, col2 FROM table3");

        redacted.Should().Contain("col1");
        redacted.Should().Contain("col2");
        redacted.Should().Contain("table3");
    }

    [Fact]
    public void Describes_a_result_value_without_disclosing_it()
    {
        Redaction.ResultValue(isNull: false, "VARCHAR").Should().Be("<value:VARCHAR>");
        Redaction.ResultValue(isNull: true, "VARCHAR").Should().Be("<null:VARCHAR>");
    }

    [Fact]
    public void Sweeps_a_formatted_message_for_hand_interpolated_secrets()
    {
        Redaction.Message("connect failed for Pwd=hunter2").Should().NotContain("hunter2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_absent_input(string? input)
    {
        Redaction.ConnectionString(input).Should().BeEmpty();
        Redaction.Sql(input).Should().BeEmpty();
        Redaction.Message(input).Should().BeEmpty();
    }
}
