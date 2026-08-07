using FluentAssertions;
using Ims.Core.Catalog;
using Ims.Core.Completion;
using Ims.Core.Sql;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>A snapshot with whatever the test wants in it, and no server behind it.</summary>
internal sealed class FakeCatalogSnapshot : ICatalogSnapshot
{
    private readonly Dictionary<string, IReadOnlyList<string>> _columns = new(StringComparer.OrdinalIgnoreCase);

    public List<SchemaObject> Loaded { get; } = [];

    public List<string> ColumnRequests { get; } = [];

    public IReadOnlyList<SchemaObject> Objects => Loaded;

    public IReadOnlyList<string> Owners { get; set; } = [];

    public void AddTable(string name, string owner, params string[] columns)
    {
        Loaded.Add(new SchemaObject
        {
            TabId = Loaded.Count + 100,
            Name = name,
            Owner = owner,
            Kind = SchemaObjectKind.Table,
        });

        _columns[name] = columns;
    }

    public IReadOnlyList<string> ColumnsOf(string name, string? owner) =>
        _columns.TryGetValue(name, out IReadOnlyList<string>? columns) ? columns : [];

    public void RequestColumns(string name, string? owner) => ColumnRequests.Add(name);
}

public class SqlTokenizerTests
{
    [Fact]
    public void Keeps_every_offset()
    {
        // The whole reason this exists rather than reusing StripLiteralsAndComments:
        // completion needs to know where the caret is, so nothing may change length.
        IReadOnlyList<SqlToken> tokens = SqlTokenizer.Tokenize("select a from t");

        tokens[0].Offset.Should().Be(0);
        tokens[1].Offset.Should().Be(7);
        tokens[2].Offset.Should().Be(9);
        tokens[3].Offset.Should().Be(14);
    }

    [Theory]
    [InlineData("-- a comment")]
    [InlineData("{ a comment }")]
    [InlineData("/* a comment */")]
    public void Recognises_all_three_Informix_comment_forms(string comment)
    {
        IReadOnlyList<SqlToken> tokens = SqlTokenizer.Tokenize(comment);

        tokens.Should().ContainSingle().Which.Kind.Should().Be(SqlTokenKind.Comment);
    }

    [Fact]
    public void A_doubled_quote_does_not_end_a_literal()
    {
        IReadOnlyList<SqlToken> tokens = SqlTokenizer.Tokenize("'it''s' x");

        tokens[0].Text.Should().Be("'it''s'");
        tokens[1].Text.Should().Be("x");
    }

    [Fact]
    public void A_delimited_identifier_reports_the_name_inside_it()
    {
        SqlTokenizer.Tokenize("\"Mixed Case\"")[0].Identifier.Should().Be("Mixed Case");
    }
}

public class CompletionContextTests
{
    private static CompletionContext At(string sql) =>
        CompletionContext.Analyse(sql.Replace("|", string.Empty, StringComparison.Ordinal), sql.IndexOf('|', StringComparison.Ordinal));

    [Fact]
    public void After_FROM_the_caret_wants_an_object()
    {
        At("select * from |").Target.Should().Be(CompletionTarget.ObjectName);
        At("select * from cus|").Target.Should().Be(CompletionTarget.ObjectName);
        At("insert into |").Target.Should().Be(CompletionTarget.ObjectName);
        At("update |").Target.Should().Be(CompletionTarget.ObjectName);
    }

    [Fact]
    public void Inside_a_predicate_the_caret_wants_a_column()
    {
        At("select * from customer where |").Target.Should().Be(CompletionTarget.ColumnOrExpression);
        At("select | from customer").Target.Should().Be(CompletionTarget.ColumnOrExpression);
        At("select * from a join b on |").Target.Should().Be(CompletionTarget.ColumnOrExpression);
    }

    [Fact]
    public void A_dot_makes_it_a_member_reference()
    {
        CompletionContext context = At("select c.| from customer c");

        context.Target.Should().Be(CompletionTarget.Member);
        context.Qualifier.Should().Be("c");
    }

    [Fact]
    public void Reads_an_owner_qualified_member()
    {
        At("select * from informix.|").Qualifier.Should().Be("informix");
    }

    [Fact]
    public void Captures_the_prefix_and_where_it_starts()
    {
        CompletionContext context = At("select * from cust|");

        context.Prefix.Should().Be("cust");
        context.ReplacementOffset.Should().Be(14);
    }

    [Fact]
    public void Finds_tables_that_appear_after_the_caret()
    {
        // "SELECT <caret> FROM customer" is the order people actually type in. A
        // context that only looked backwards would be useless in the commonest case.
        CompletionContext context = At("select | from customer");

        context.Tables.Should().ContainSingle().Which.Name.Should().Be("customer");
    }

    [Fact]
    public void Reads_an_alias_with_or_without_AS()
    {
        At("select * from customer c where |").Tables[0].Alias.Should().Be("c");
        At("select * from customer as c where |").Tables[0].Alias.Should().Be("c");
    }

    [Fact]
    public void Reads_an_owner_qualified_table()
    {
        TableReference table = At("select * from informix.customer c where |").Tables[0];

        table.Owner.Should().Be("informix");
        table.Name.Should().Be("customer");
        table.Alias.Should().Be("c");
    }

    [Fact]
    public void Does_not_take_a_join_keyword_for_an_alias()
    {
        // "FROM a LEFT JOIN b" must not decide that a is aliased LEFT.
        IReadOnlyList<TableReference> tables = At("select * from orders o left outer join items i on |").Tables;

        tables.Should().HaveCount(2);
        tables[0].Alias.Should().Be("o");
        tables[1].Name.Should().Be("items");
        tables[1].Alias.Should().Be("i");
    }

    [Fact]
    public void Reads_a_comma_separated_from_list()
    {
        IReadOnlyList<TableReference> tables = At("select * from a x, b y where |").Tables;

        tables.Should().HaveCount(2);
        tables[1].Name.Should().Be("b");
    }

    [Fact]
    public void Sees_through_Informix_old_style_outer_join_syntax()
    {
        IReadOnlyList<TableReference> tables = At("select * from customer c, outer(orders o) where |").Tables;

        tables.Select(t => t.Name).Should().Contain(["customer", "orders"]);
    }

    [Fact]
    public void Only_looks_at_the_statement_the_caret_is_in()
    {
        // A caret in the second statement must not be offered the first's tables.
        CompletionContext context = At("select * from alpha; select * from beta where |");

        context.Tables.Should().ContainSingle().Which.Name.Should().Be("beta");
    }

    [Fact]
    public void A_keyword_inside_a_comment_does_not_start_a_clause()
    {
        // Informix has three comment forms and all three have to be seen through, or
        // the list is wrong exactly where someone was explaining what they meant.
        At("select * from customer -- where\n |")
            .Target.Should().NotBe(CompletionTarget.ColumnOrExpression);
    }

    [Fact]
    public void An_empty_script_asks_for_anything()
    {
        CompletionContext context = CompletionContext.Analyse(string.Empty, 0);

        context.Target.Should().Be(CompletionTarget.Anything);
        context.Prefix.Should().BeEmpty();
    }

    [Fact]
    public void A_caret_past_the_end_is_clamped_rather_than_throwing()
    {
        CompletionContext.Analyse("select", 9999).Should().NotBeNull();
    }
}

public class CompletionEngineTests
{
    private static CompletionContext At(string sql) =>
        CompletionContext.Analyse(sql.Replace("|", string.Empty, StringComparison.Ordinal), sql.IndexOf('|', StringComparison.Ordinal));

    private static FakeCatalogSnapshot Schema()
    {
        var catalog = new FakeCatalogSnapshot { Owners = ["informix", "sales"] };

        catalog.AddTable("customer", "informix", "customer_num", "fname", "lname");
        catalog.AddTable("orders", "informix", "order_num", "customer_num", "order_date");

        return catalog;
    }

    [Fact]
    public void After_FROM_it_offers_tables_and_not_the_language()
    {
        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select * from |"), Schema());

        items.Select(i => i.Text).Should().Contain(["customer", "orders"]);
        items.Should().NotContain(i => i.Kind == CompletionKind.BuiltInFunction);
    }

    [Fact]
    public void In_a_predicate_the_columns_of_the_tables_in_scope_come_first()
    {
        // A list that puts ABS above the columns of the table you just named is a list
        // you scroll past, which is the same as not having one.
        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select * from customer where |"), Schema());

        items[0].Kind.Should().BeOneOf(CompletionKind.Alias, CompletionKind.Column);
        items.Take(4).Select(i => i.Text).Should().Contain("lname");
    }

    [Fact]
    public void An_alias_resolves_to_its_own_tables_columns()
    {
        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select o.| from customer c, orders o"), Schema());

        items.Select(i => i.Text).Should().BeEquivalentTo(["order_num", "customer_num", "order_date"]);
    }

    [Fact]
    public void An_alias_shadows_a_table_of_the_same_name()
    {
        // For the length of the statement the alias is what the writer meant, and
        // offering the other table's columns would be actively misleading.
        var catalog = Schema();
        catalog.AddTable("c", "informix", "unrelated_column");

        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select c.| from customer c"), catalog);

        items.Select(i => i.Text).Should().Contain("lname").And.NotContain("unrelated_column");
    }

    [Fact]
    public void An_owner_before_the_dot_offers_what_that_owner_owns()
    {
        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select * from informix.|"), Schema());

        items.Select(i => i.Text).Should().Contain(["customer", "orders"]);
    }

    [Fact]
    public void A_column_shared_by_two_joined_tables_is_offered_once()
    {
        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select * from customer c, orders o where |"), Schema());

        items.Where(i => i.Text == "customer_num" && i.Kind == CompletionKind.Column)
            .Should().ContainSingle();
    }

    [Fact]
    public void Asks_for_columns_it_does_not_have_rather_than_blocking_for_them()
    {
        // The contract that keeps the caret responsive (NFR-1): return what is cached,
        // and ask for the rest so the next keystroke has it.
        var catalog = new FakeCatalogSnapshot();
        catalog.Loaded.Add(new SchemaObject
        {
            TabId = 100,
            Name = "customer",
            Owner = "informix",
            Kind = SchemaObjectKind.Table,
        });

        CompletionEngine.Suggest(At("select * from customer where |"), catalog);

        catalog.ColumnRequests.Should().Contain("customer");
    }

    [Fact]
    public void A_prefix_match_outranks_a_contains_match()
    {
        var catalog = new FakeCatalogSnapshot();
        catalog.AddTable("order_customer", "informix");
        catalog.AddTable("customer", "informix");

        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("select * from cust|"), catalog);

        items[0].Text.Should().Be("customer");
        items.Select(i => i.Text).Should().Contain("order_customer");
    }

    [Fact]
    public void Filtering_is_case_insensitive()
    {
        CompletionEngine.Suggest(At("select * from CUST|"), Schema())
            .Select(i => i.Text).Should().Contain("customer");
    }

    [Fact]
    public void With_no_connection_it_still_offers_the_language()
    {
        // PR-8.3: someone drafting a script offline gets the Informix vocabulary, which
        // is most of what the teaching requirement is about.
        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(
            At("sel|"), EmptyCatalogSnapshot.Instance);

        items.Select(i => i.Text).Should().Contain("SELECT");
    }

    [Fact]
    public void Never_returns_more_than_it_promises()
    {
        var catalog = new FakeCatalogSnapshot();

        for (var i = 0; i < 5000; i++)
        {
            catalog.AddTable("table_" + i, "informix");
        }

        CompletionEngine.Suggest(At("select * from |"), catalog)
            .Should().HaveCountLessThanOrEqualTo(CompletionEngine.MaximumItems);
    }
}

public class InformixVocabularyTests
{
    [Fact]
    public void Carries_the_Informix_specific_words_a_generic_tool_would_miss()
    {
        IEnumerable<string> words = InformixVocabulary.All.Select(i => i.Text);

        words.Should().Contain(["FIRST", "SKIP", "MATCHES", "LVARCHAR", "SERIAL", "DBINFO", "NVL"]);
    }

    [Fact]
    public void Explains_the_ones_that_would_otherwise_mislead()
    {
        // PR-8.3. MATCHES looks like LIKE and is not; being told so is the difference
        // between five seconds and an hour.
        CompletionItem matches = InformixVocabulary.All.First(i => i.Text == "MATCHES");

        matches.Detail.Should().Contain("*").And.Contain("?");
    }

    [Fact]
    public void An_explanation_never_just_restates_the_word()
    {
        // A detail column that says "SELECT: selects" trains people to stop reading it.
        foreach (CompletionItem item in InformixVocabulary.All.Where(i => i.Detail is not null))
        {
            item.Detail.Should().NotBe(item.Text);
        }
    }

    [Fact]
    public void Every_entry_is_listed_once()
    {
        InformixVocabulary.All
            .GroupBy(i => (i.Text, i.Kind))
            .Where(g => g.Count() > 1)
            .Should().BeEmpty();
    }
}
