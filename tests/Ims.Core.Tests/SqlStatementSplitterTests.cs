using FluentAssertions;
using Ims.Core.Sql;
using Xunit;

namespace Ims.Core.Tests;

public class SqlStatementSplitterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void An_empty_script_has_no_statements(string? script)
    {
        SqlStatementSplitter.Split(script).Should().BeEmpty();
    }

    [Fact]
    public void Splits_on_semicolons()
    {
        var statements = SqlStatementSplitter.Split(
            "SELECT 1 FROM systables; SELECT 2 FROM systables;");

        statements.Should().HaveCount(2);
        statements[0].Text.Should().Be("SELECT 1 FROM systables");
        statements[1].Text.Should().Be("SELECT 2 FROM systables");
    }

    [Fact]
    public void Keeps_a_final_statement_with_no_terminator()
    {
        var statements = SqlStatementSplitter.Split("SELECT 1 FROM systables");

        statements.Should().ContainSingle();
        statements[0].Text.Should().Be("SELECT 1 FROM systables");
    }

    [Fact]
    public void Reports_offset_and_line_so_the_editor_can_point_at_a_failure()
    {
        // PR-3.4: "indicating clearly which statement failed".
        const string script = "SELECT 1 FROM t;\nSELECT 2 FROM t;\n\nSELECT 3 FROM t;";

        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(3);
        statements[0].LineNumber.Should().Be(1);
        statements[1].LineNumber.Should().Be(2);
        statements[2].LineNumber.Should().Be(4);

        foreach (SqlStatement statement in statements)
        {
            script.Substring(statement.Offset, statement.Text.Length).Should().Be(statement.Text);
        }
    }

    [Fact]
    public void A_semicolon_inside_a_string_literal_is_not_a_terminator()
    {
        var statements = SqlStatementSplitter.Split(
            "INSERT INTO t VALUES ('a;b'); SELECT 1 FROM t");

        statements.Should().HaveCount(2);
        statements[0].Text.Should().Be("INSERT INTO t VALUES ('a;b')");
    }

    [Fact]
    public void Handles_the_doubled_quote_escape()
    {
        var statements = SqlStatementSplitter.Split(
            "INSERT INTO t VALUES ('O''Brien; Esq'); SELECT 1 FROM t");

        statements.Should().HaveCount(2);
        statements[0].Text.Should().Be("INSERT INTO t VALUES ('O''Brien; Esq')");
    }

    [Fact]
    public void A_semicolon_inside_a_delimited_identifier_is_not_a_terminator()
    {
        var statements = SqlStatementSplitter.Split("SELECT \"odd;name\" FROM t; SELECT 1 FROM t");

        statements.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("SELECT 1 FROM t; -- trailing; comment\nSELECT 2 FROM t")]
    [InlineData("SELECT 1 FROM t; { braced; comment }\nSELECT 2 FROM t")]
    [InlineData("SELECT 1 FROM t; /* block; comment */ SELECT 2 FROM t")]
    public void A_semicolon_inside_any_Informix_comment_form_is_not_a_terminator(string script)
    {
        // Informix has three comment forms and IMS must honour all of them.
        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(2);
        statements[0].Text.Should().Be("SELECT 1 FROM t");
    }

    [Fact]
    public void A_script_of_only_comments_yields_no_statements()
    {
        SqlStatementSplitter.Split("-- nothing here\n{ nor here }\n/* nor here */")
            .Should().BeEmpty();
    }

    [Fact]
    public void Empty_statements_between_semicolons_are_dropped()
    {
        var statements = SqlStatementSplitter.Split("SELECT 1 FROM t;;;SELECT 2 FROM t;");

        statements.Should().HaveCount(2);
    }

    // ---- SPL: the case that matters most on Informix ---------------------------

    [Fact]
    public void An_SPL_procedure_body_is_not_torn_apart_by_its_own_semicolons()
    {
        // The single most visible way a SQL tool announces it does not understand
        // Informix. A naive split produces five broken fragments here.
        const string script = """
            CREATE PROCEDURE p_test()
                DEFINE i INT;
                LET i = 0;
                WHILE i < 10
                    LET i = i + 1;
                END WHILE;
            END PROCEDURE;

            SELECT 1 FROM systables;
            """;

        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(2);
        statements[0].Text.Should().StartWith("CREATE PROCEDURE p_test()");
        statements[0].Text.Should().EndWith("END PROCEDURE");
        statements[0].Text.Should().Contain("LET i = i + 1;");
        statements[1].Text.Should().Be("SELECT 1 FROM systables");
    }

    [Fact]
    public void An_SPL_function_body_is_kept_whole()
    {
        const string script = """
            CREATE FUNCTION f_double(n INT) RETURNING INT;
                RETURN n * 2;
            END FUNCTION;
            SELECT f_double(2) FROM systables;
            """;

        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(2);
        statements[0].Text.Should().EndWith("END FUNCTION");
    }

    [Fact]
    public void Recognises_the_DBA_and_OR_REPLACE_variants()
    {
        const string script = """
            CREATE DBA PROCEDURE p_a()
                LET x = 1;
            END PROCEDURE;
            CREATE OR REPLACE FUNCTION f_b() RETURNING INT;
                RETURN 1;
            END FUNCTION;
            """;

        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(2);
        statements[0].Text.Should().EndWith("END PROCEDURE");
        statements[1].Text.Should().EndWith("END FUNCTION");
    }

    [Fact]
    public void A_statement_after_a_routine_still_splits_normally()
    {
        const string script = """
            CREATE PROCEDURE p()
                LET x = 1;
            END PROCEDURE;
            UPDATE t SET a = 1;
            DELETE FROM t;
            """;

        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(3);
        statements[1].Text.Should().Be("UPDATE t SET a = 1");
        statements[2].Text.Should().Be("DELETE FROM t");
    }

    [Fact]
    public void CREATED_is_not_mistaken_for_CREATE()
    {
        // Whole-word matching, or a column called "created" starts a routine body
        // and swallows the rest of the script.
        var statements = SqlStatementSplitter.Split(
            "SELECT created FROM t; SELECT 2 FROM t;");

        statements.Should().HaveCount(2);
    }

    [Fact]
    public void A_create_table_is_not_treated_as_a_routine()
    {
        const string script = """
            CREATE TABLE t (id INT, name VARCHAR(50));
            SELECT 1 FROM t;
            """;

        var statements = SqlStatementSplitter.Split(script);

        statements.Should().HaveCount(2);
    }

    [Fact]
    public void An_unterminated_string_does_not_hang_the_splitter()
    {
        // The server should report the syntax error, not IMS (PR-8.2). What matters
        // here is that the splitter terminates.
        var statements = SqlStatementSplitter.Split("SELECT 'unterminated FROM t");

        statements.Should().ContainSingle();
    }
}
