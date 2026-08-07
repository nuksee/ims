using FluentAssertions;
using Ims.Core.History;
using Xunit;

namespace Ims.Core.Tests;

public class QueryHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"ims-history-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        foreach (string path in (string[])[_path, _path + ".tmp"])
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    private static QueryHistoryEntry Entry(
        string sql = "SELECT 1 FROM systables",
        string target = "Dev (ol_dev/stores)",
        bool succeeded = true,
        long? rows = 1) => new()
        {
            ExecutedAt = DateTimeOffset.Now,
            Sql = sql,
            Target = target,
            Database = "stores",
            ElapsedMilliseconds = 12.5,
            RowCount = rows,
            Succeeded = succeeded,
        };

    [Fact]
    public void Reading_an_absent_history_is_not_an_error()
    {
        new QueryHistory(_path).Read().Should().BeEmpty();
    }

    [Fact]
    public void Records_everything_PR_3_12_asks_for()
    {
        var history = new QueryHistory(_path);
        history.Add(Entry());

        QueryHistoryEntry stored = history.Read().Should().ContainSingle().Subject;

        stored.Sql.Should().Be("SELECT 1 FROM systables");
        stored.Target.Should().Be("Dev (ol_dev/stores)");
        stored.ElapsedMilliseconds.Should().Be(12.5);
        stored.RowCount.Should().Be(1);
        stored.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Returns_the_newest_first()
    {
        var history = new QueryHistory(_path);
        history.Add(Entry(sql: "SELECT 1 FROM t"));
        history.Add(Entry(sql: "SELECT 2 FROM t"));
        history.Add(Entry(sql: "SELECT 3 FROM t"));

        history.Read().Select(e => e.Sql)
            .Should().ContainInOrder("SELECT 3 FROM t", "SELECT 2 FROM t", "SELECT 1 FROM t");
    }

    [Fact]
    public void Keeps_the_statement_verbatim()
    {
        // Unlike the application log. PR-6.3 governs logs; PR-3.12 is the user's own
        // record of their own work, and a history with the literals stripped could
        // not answer "what did I run yesterday".
        const string sql = "SELECT * FROM patient WHERE health_card = '1234567890'";

        var history = new QueryHistory(_path);
        history.Add(Entry(sql: sql));

        history.Read()[0].Sql.Should().Be(sql);
    }

    [Fact]
    public void Records_a_failure_with_its_reason()
    {
        var history = new QueryHistory(_path);

        history.Add(Entry(sql: "SELECT * FROM nope", succeeded: false, rows: null) with
        {
            Error = "SQLCODE -206: table not found",
        });

        QueryHistoryEntry stored = history.Read()[0];

        stored.Succeeded.Should().BeFalse();
        stored.Error.Should().Contain("-206");
        stored.RowCount.Should().BeNull();
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("PATIENT")]
    [InlineData("ol_dev")]
    [InlineData("stores")]
    public void Search_finds_by_statement_target_or_database(string term)
    {
        var history = new QueryHistory(_path);
        history.Add(Entry(sql: "SELECT * FROM patient"));
        history.Add(Entry(sql: "SELECT 1 FROM t", target: "Other (ol_other/scratch)") with
        {
            Database = "scratch",
        });

        history.Search(term).Should().NotBeEmpty();
    }

    [Fact]
    public void A_corrupt_line_does_not_cost_the_rest_of_the_history()
    {
        var history = new QueryHistory(_path);
        history.Add(Entry(sql: "SELECT 1 FROM t"));

        File.AppendAllLines(_path, ["{ not valid json"]);

        history.Add(Entry(sql: "SELECT 2 FROM t"));

        history.Read().Should().HaveCount(2);
    }

    [Fact]
    public void Trim_keeps_the_newest_entries()
    {
        var history = new QueryHistory(_path, maximumEntries: 3);

        for (int i = 1; i <= 10; i++)
        {
            history.Add(Entry(sql: $"SELECT {i} FROM t"));
        }

        history.Trim();

        var remaining = history.Read();

        remaining.Should().HaveCount(3);
        remaining[0].Sql.Should().Be("SELECT 10 FROM t");
        remaining[2].Sql.Should().Be("SELECT 8 FROM t");
    }

    [Fact]
    public void A_write_failure_never_costs_the_user_their_work()
    {
        // Losing a history entry is a nuisance; an exception mid-execution is not.
        var history = new QueryHistory(Path.Combine("Z:", "no-such-drive", "history.jsonl"));

        history.Invoking(h => h.Add(Entry())).Should().NotThrow();
    }
}
