using FluentAssertions;
using Ims.Core.Sql;
using Xunit;

namespace Ims.Core.Tests;

public class StatementSafetyTests
{
    [Theory]
    [InlineData("DELETE FROM patient")]
    [InlineData("delete from patient")]
    [InlineData("DELETE patient")]
    public void Warns_on_an_unqualified_DELETE(string sql)
    {
        StatementSafety.Check(sql).Should().NotBeNull()
            .And.Subject.As<StatementWarning>().Requirement.Should().Be("PR-3.8");
    }

    [Theory]
    [InlineData("UPDATE patient SET active = 'f'")]
    [InlineData("update patient set active = 'f'")]
    public void Warns_on_an_unqualified_UPDATE(string sql)
    {
        StatementSafety.Check(sql).Should().NotBeNull();
    }

    [Theory]
    [InlineData("DELETE FROM patient WHERE id = 1")]
    [InlineData("UPDATE patient SET active = 'f' WHERE id = 1")]
    [InlineData("update patient set active = 'f' where id = 1")]
    public void Does_not_warn_when_there_is_a_WHERE(string sql)
    {
        StatementSafety.Check(sql).Should().BeNull();
    }

    [Theory]
    [InlineData("SELECT * FROM patient")]
    [InlineData("INSERT INTO patient VALUES (1)")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("TRUNCATE TABLE patient")]
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_warn_on_anything_PR_3_8_did_not_ask_about(string? sql)
    {
        // Deliberately narrow. DEC-2 says Informix privileges are the real control,
        // and warning about everything trains people to click through warnings.
        StatementSafety.Check(sql).Should().BeNull();
    }

    [Fact]
    public void A_WHERE_inside_a_string_literal_does_not_count()
    {
        // The dangerous false negative: this deletes every row.
        StatementSafety.Check("UPDATE t SET note = 'see the WHERE clause'")
            .Should().NotBeNull();
    }

    [Fact]
    public void A_WHERE_inside_a_comment_does_not_count()
    {
        StatementSafety.Check("DELETE FROM audit -- WHERE created < TODAY")
            .Should().NotBeNull();

        StatementSafety.Check("DELETE FROM audit { WHERE created < TODAY }")
            .Should().NotBeNull();

        StatementSafety.Check("DELETE FROM audit /* WHERE created < TODAY */")
            .Should().NotBeNull();
    }

    [Fact]
    public void A_leading_comment_does_not_hide_the_statement_keyword()
    {
        StatementSafety.Check("-- clean up\nDELETE FROM audit").Should().NotBeNull();
    }

    [Fact]
    public void WHEREVER_is_not_a_WHERE()
    {
        // Whole-word matching, or an identifier merely containing "where" silences
        // the warning.
        StatementSafety.Check("UPDATE t SET wherever = 1").Should().NotBeNull();
    }

    [Fact]
    public void A_positioned_update_is_bounded_by_its_cursor()
    {
        StatementSafety.Check("UPDATE t SET a = 1 WHERE CURRENT OF c1").Should().BeNull();
    }

    [Fact]
    public void Checking_a_script_reports_only_the_statements_that_need_it()
    {
        var statements = SqlStatementSplitter.Split(
            """
            SELECT * FROM t;
            DELETE FROM audit;
            UPDATE t SET a = 1 WHERE id = 2;
            UPDATE t SET a = 1;
            """);

        var warnings = StatementSafety.CheckScript(statements);

        warnings.Should().HaveCount(2);
        warnings[0].Statement.Text.Should().Be("DELETE FROM audit");
        warnings[1].Statement.Text.Should().Be("UPDATE t SET a = 1");
    }

    [Fact]
    public void The_warning_says_what_will_happen()
    {
        StatementWarning? warning = StatementSafety.Check("DELETE FROM patient");

        warning!.Title.Should().Contain("DELETE");
        warning.Detail.Should().Contain("every row");
    }
}
