using FluentAssertions;
using Ims.Core.Editing;
using Xunit;

namespace Ims.Core.Tests;

public class EditorAutosaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"ims-autosave-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Recovering_with_nothing_saved_returns_nothing()
    {
        new EditorAutosave(_directory).Recover().Should().BeEmpty();
    }

    [Fact]
    public void Unsaved_content_survives_the_process()
    {
        // Slice 1's acceptance criterion, in one test: what a new instance of the
        // autosave sees is what a restarted IMS would recover.
        var autosave = new EditorAutosave(_directory);
        autosave.Save("tab-1", "Query 1", "SELECT * FROM systables", filePath: null);

        var afterRestart = new EditorAutosave(_directory);
        IReadOnlyList<AutosavedTab> recovered = afterRestart.Recover();

        recovered.Should().ContainSingle();
        recovered[0].Title.Should().Be("Query 1");
        recovered[0].Sql.Should().Be("SELECT * FROM systables");
    }

    [Fact]
    public void Saving_the_same_tab_again_replaces_it()
    {
        var autosave = new EditorAutosave(_directory);

        autosave.Save("tab-1", "Query 1", "SELECT 1 FROM t", null);
        autosave.Save("tab-1", "Query 1", "SELECT 2 FROM t", null);

        autosave.Recover().Should().ContainSingle()
            .Which.Sql.Should().Be("SELECT 2 FROM t");
    }

    [Fact]
    public void A_discarded_tab_does_not_come_back()
    {
        var autosave = new EditorAutosave(_directory);

        autosave.Save("tab-1", "Query 1", "SELECT 1 FROM t", null);
        autosave.Discard("tab-1");

        autosave.Recover().Should().BeEmpty();
    }

    [Fact]
    public void Several_tabs_are_recovered_newest_first()
    {
        var autosave = new EditorAutosave(_directory);

        autosave.Save("tab-1", "First", "SELECT 1 FROM t", null);
        Thread.Sleep(10);
        autosave.Save("tab-2", "Second", "SELECT 2 FROM t", null);

        autosave.Recover().Select(t => t.Title)
            .Should().ContainInOrder("Second", "First");
    }

    [Fact]
    public void An_empty_tab_is_not_worth_recovering()
    {
        var autosave = new EditorAutosave(_directory);
        autosave.Save("tab-1", "Query 1", "   ", null);

        autosave.Recover().Should().BeEmpty();
    }

    [Fact]
    public void A_corrupt_file_does_not_cost_the_other_tabs()
    {
        var autosave = new EditorAutosave(_directory);
        autosave.Save("tab-1", "Good", "SELECT 1 FROM t", null);

        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ not json");

        autosave.Recover().Should().ContainSingle().Which.Title.Should().Be("Good");
    }

    [Fact]
    public void A_tab_title_cannot_escape_the_autosave_directory()
    {
        // Tabs are often named after files, and a path-shaped title must not be
        // able to write outside where it belongs.
        var autosave = new EditorAutosave(_directory);

        autosave.Save(@"..\..\escape", "Escape", "SELECT 1 FROM t", null);

        Directory.GetFiles(_directory).Should().ContainSingle();
        File.Exists(Path.Combine(Path.GetTempPath(), "escape.json")).Should().BeFalse();
    }

    [Fact]
    public void The_file_path_survives_so_a_recovered_tab_knows_where_it_came_from()
    {
        var autosave = new EditorAutosave(_directory);
        autosave.Save("tab-1", "report.sql", "SELECT 1 FROM t", @"C:\work\report.sql");

        autosave.Recover()[0].FilePath.Should().Be(@"C:\work\report.sql");
    }

    [Fact]
    public void DiscardAll_clears_everything()
    {
        var autosave = new EditorAutosave(_directory);
        autosave.Save("tab-1", "A", "SELECT 1 FROM t", null);
        autosave.Save("tab-2", "B", "SELECT 2 FROM t", null);

        autosave.DiscardAll();

        autosave.Recover().Should().BeEmpty();
    }

    [Fact]
    public void A_write_failure_never_interrupts_typing()
    {
        var autosave = new EditorAutosave(Path.Combine("Z:", "no-such-drive", "autosave"));

        autosave.Invoking(a => a.Save("tab-1", "A", "SELECT 1 FROM t", null))
            .Should().NotThrow();
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        var autosave = new EditorAutosave(_directory);
        autosave.Save("tab-1", "A", "SELECT 1 FROM t", null);

        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }
}
