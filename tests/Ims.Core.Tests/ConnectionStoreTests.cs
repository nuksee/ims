using FluentAssertions;
using Ims.Core.Connections;
using Xunit;

namespace Ims.Core.Tests;

public class ConnectionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"ims-connections-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        GC.SuppressFinalize(this);
    }

    private static ConnectionDescriptor Descriptor(
        string name = "Dev",
        InformixEnvironment environment = InformixEnvironment.Development,
        string? group = null,
        Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            DisplayName = name,
            ServerName = "ol_" + name.ToLowerInvariant(),
            Host = "192.0.2.10",
            Service = "9088",
            Database = "stores",
            UserName = "kaveh",
            Environment = environment,
            Group = group,
        };

    [Fact]
    public void Loading_a_store_that_does_not_exist_yet_is_not_an_error()
    {
        var store = new ConnectionStore(_path);

        store.Invoking(s => s.Load()).Should().NotThrow();
        store.Connections.Should().BeEmpty();
    }

    [Fact]
    public void Round_trips_a_connection()
    {
        var store = new ConnectionStore(_path);
        ConnectionDescriptor descriptor = Descriptor();
        store.AddOrUpdate(descriptor);
        store.Save();

        var reloaded = new ConnectionStore(_path);
        reloaded.Load();

        reloaded.Connections.Should().ContainSingle();
        reloaded.Connections[0].Should().Be(descriptor);
    }

    [Fact]
    public void The_saved_file_contains_no_secret()
    {
        // PR-1.4 and DEC-9: "never in a plain-text or user-readable config file".
        // ConnectionDescriptor has no password field, so this holds by construction
        // — but it is the kind of thing worth a test, because adding one later would
        // be an easy mistake and a serious one.
        var store = new ConnectionStore(_path);
        store.AddOrUpdate(Descriptor());
        store.Save();

        string json = File.ReadAllText(_path);

        foreach (string forbidden in (string[])["password", "pwd", "secret", "token", "credential"])
        {
            json.Should().NotContainEquivalentOf(
                forbidden,
                "credentials belong in Windows Credential Manager, not this file");
        }
    }

    [Fact]
    public void AddOrUpdate_replaces_the_entry_with_the_same_id()
    {
        var id = Guid.NewGuid();
        var store = new ConnectionStore(_path);

        store.AddOrUpdate(Descriptor("First", id: id));
        store.AddOrUpdate(Descriptor("Second", id: id));

        store.Connections.Should().ContainSingle();
        store.Connections[0].DisplayName.Should().Be("Second");
    }

    [Fact]
    public void Remove_reports_whether_there_was_anything_to_remove()
    {
        var store = new ConnectionStore(_path);
        ConnectionDescriptor descriptor = Descriptor();
        store.AddOrUpdate(descriptor);

        store.Remove(descriptor.Id).Should().BeTrue();
        store.Remove(descriptor.Id).Should().BeFalse();
    }

    [Theory]
    [InlineData("prod")]        // display name
    [InlineData("ol_prod")]     // server name
    [InlineData("192.0.2.1")]   // host
    [InlineData("billing")]     // database
    [InlineData("PROD")]        // case-insensitive
    [InlineData("Finance")]     // group
    public void Search_matches_the_things_a_user_would_type(string term)
    {
        var store = new ConnectionStore(_path);

        store.AddOrUpdate(Descriptor("Prod", InformixEnvironment.Production, group: "Finance") with
        {
            Host = "192.0.2.1",
            Database = "billing",
        });

        store.AddOrUpdate(Descriptor("Other", InformixEnvironment.Development) with
        {
            Host = "192.0.2.9",
            Database = "scratch",
        });

        store.Search(term).Should().ContainSingle()
            .Which.DisplayName.Should().Be("Prod");
    }

    [Fact]
    public void An_empty_search_returns_everything()
    {
        var store = new ConnectionStore(_path);
        store.AddOrUpdate(Descriptor("A"));
        store.AddOrUpdate(Descriptor("B"));

        store.Search("   ").Should().HaveCount(2);
    }

    [Fact]
    public void Grouping_puts_production_first()
    {
        // PR-1.5 is about a production connection never being mistaken for anything
        // else, and ordering is part of that.
        var store = new ConnectionStore(_path);
        store.AddOrUpdate(Descriptor("D", InformixEnvironment.Development));
        store.AddOrUpdate(Descriptor("P", InformixEnvironment.Production));
        store.AddOrUpdate(Descriptor("U", InformixEnvironment.Uat));

        var groups = store.Grouped();

        groups.Select(g => g.Key).Should().ContainInOrder("Production", "UAT", "Development");
    }

    [Fact]
    public void An_explicit_group_overrides_the_environment_grouping()
    {
        var store = new ConnectionStore(_path);
        store.AddOrUpdate(Descriptor("A", InformixEnvironment.Development, group: "Billing"));

        store.Grouped().Should().ContainSingle().Which.Key.Should().Be("Billing");
    }

    [Fact]
    public void A_corrupt_file_is_reported_rather_than_silently_discarded()
    {
        // Losing someone's instance list without saying so would be worse than
        // failing to start.
        File.WriteAllText(_path, "{ this is not valid json");

        var store = new ConnectionStore(_path);

        store.Invoking(s => s.Load())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*could not be read*");

        File.Exists(_path).Should().BeTrue("the file must be left for the user to recover");
    }

    [Fact]
    public void An_interrupted_save_cannot_destroy_the_previous_list()
    {
        // Save writes to a temporary file and moves it into place.
        var store = new ConnectionStore(_path);
        store.AddOrUpdate(Descriptor("A"));
        store.Save();

        File.Exists(_path + ".tmp").Should().BeFalse("the temporary file is moved, not left behind");
        File.ReadAllText(_path).Should().Contain("\"DisplayName\"");
    }

    [Fact]
    public void The_production_flag_survives_a_round_trip()
    {
        var store = new ConnectionStore(_path);
        store.AddOrUpdate(Descriptor("P", InformixEnvironment.Production));
        store.Save();

        var reloaded = new ConnectionStore(_path);
        reloaded.Load();

        reloaded.Connections[0].IsProduction.Should().BeTrue();
        reloaded.Connections[0].Environment.Should().Be(InformixEnvironment.Production);
    }
}
