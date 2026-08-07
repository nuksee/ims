using FluentAssertions;
using Ims.Core.Catalog;
using Ims.Core.Data;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// PR-2.6, checked against the shape <c>dbschema</c> produces.
/// </summary>
/// <remarks>
/// The requirement's own acceptance test is a diff against real <c>dbschema</c>
/// output. <c>dbschema</c> ships with the Informix server rather than the client SDK,
/// so it is not on the development machine and the diff has to be run by someone with
/// server access — recorded as an open verification in docs/IMPLEMENTATION-TODO.md.
/// <para>
/// What these tests do instead is pin the output against the format transcribed from
/// <c>dbschema</c>'s documented and widely-published output, so that the diff, when it
/// is run, is against a known and deliberate baseline rather than whatever the code
/// happened to emit that day.
/// </para>
/// </remarks>
public class DdlScripterTests
{
    private static ColumnDetail Column(
        int position,
        string name,
        string type,
        InformixDbType dbType = InformixDbType.Char,
        bool nullable = true,
        string? defaultValue = null) =>
        new()
        {
            Position = position,
            Name = name,
            DbType = dbType,
            TypeDescription = type,
            IsNullable = nullable,
            DefaultValue = defaultValue,
            RawColType = 0,
            RawColLength = 0,
        };

    private static TableDetail Table(
        IReadOnlyList<ColumnDetail>? columns = null,
        IReadOnlyList<IndexDetail>? indexes = null,
        IReadOnlyList<ConstraintDetail>? constraints = null,
        IReadOnlyList<TriggerDetail>? triggers = null,
        IReadOnlyList<FragmentDetail>? fragments = null,
        string lockMode = "page",
        string? dbSpace = null) =>
        new()
        {
            Object = new SchemaObject
            {
                TabId = 100,
                Name = "customer",
                Owner = "informix",
                Kind = SchemaObjectKind.Table,
            },
            Columns = columns ?? [Column(1, "customer_num", "SERIAL", InformixDbType.Serial, false)],
            Indexes = indexes ?? [],
            Constraints = constraints ?? [],
            Triggers = triggers ?? [],
            Fragments = fragments ?? [],
            LockMode = lockMode == "page" ? "Page" : lockMode,
            FirstExtentKb = 16,
            NextExtentKb = 16,
            DbSpace = dbSpace,
            QueriesUsed = [],
        };

    [Fact]
    public void Scripts_a_table_the_way_dbschema_lays_it_out()
    {
        TableDetail detail = Table(
            columns:
            [
                Column(1, "customer_num", "SERIAL", InformixDbType.Serial, nullable: false),
                Column(2, "fname", "CHAR(15)"),
                Column(3, "lname", "CHAR(15)"),
            ],
            constraints:
            [
                new ConstraintDetail
                {
                    Name = "u100_1",
                    Owner = "informix",
                    Kind = ConstraintKind.PrimaryKey,
                    Columns = ["customer_num"],
                    RawConstraintType = 'P',
                },
            ]);

        string sql = DdlScripter.ScriptTable(detail);

        sql.Should().Contain("create table \"informix\".customer");
        sql.Should().Contain("    customer_num serial,");
        sql.Should().Contain("    fname char(15),");
        sql.Should().Contain(
            "    primary key (customer_num) constraint \"informix\".u100_1");
        sql.Should().Contain("  ) extent size 16 next size 16 lock mode page;");
    }

    [Fact]
    public void The_last_clause_carries_no_comma()
    {
        // A trailing comma before the closing bracket is a syntax error, and it is the
        // failure a generated column list reaches for first.
        string sql = DdlScripter.ScriptTable(Table(
            columns: [Column(1, "a", "INTEGER", InformixDbType.Integer), Column(2, "b", "CHAR(1)")]));

        sql.Should().NotContain(",\r\n  )").And.NotContain(",\n  )");
    }

    [Fact]
    public void Marks_a_column_not_null()
    {
        string sql = DdlScripter.ScriptTable(Table(
            columns: [Column(1, "code", "CHAR(4)", InformixDbType.Char, nullable: false)]));

        sql.Should().Contain("code char(4) not null");
    }

    [Fact]
    public void Leaves_not_null_off_a_serial()
    {
        // SERIAL cannot be null and Informix rejects the words on some paths. dbschema
        // writes them anyway; a script that runs matters more than a byte-exact diff.
        string sql = DdlScripter.ScriptTable(Table(
            columns: [Column(1, "id", "SERIAL", InformixDbType.Serial, nullable: false)]));

        sql.Should().Contain("    id serial").And.NotContain("serial not null");
    }

    [Fact]
    public void Quotes_a_character_default_and_not_a_numeric_one()
    {
        // An unquoted character default is either a syntax error or — for a value that
        // parses as an identifier — silently something else.
        string sql = DdlScripter.ScriptTable(Table(columns:
        [
            Column(1, "status", "CHAR(1)", InformixDbType.Char, defaultValue: "A"),
            Column(2, "qty", "INTEGER", InformixDbType.Integer, defaultValue: "0"),
        ]));

        sql.Should().Contain("status char(1) default 'A'");
        sql.Should().Contain("qty integer default 0");
    }

    [Theory]
    [InlineData("CURRENT", "current")]
    [InlineData("TODAY", "today")]
    [InlineData("USER", "user")]
    [InlineData("NULL", "null")]
    [InlineData("DBSERVERNAME", "dbservername")]
    public void A_keyword_default_is_never_quoted(string stored, string expected)
    {
        string sql = DdlScripter.ScriptTable(Table(columns:
        [
            Column(1, "created", "DATETIME YEAR TO SECOND", InformixDbType.DateTime, defaultValue: stored),
        ]));

        sql.Should().Contain("default " + expected).And.NotContain("'" + stored + "'");
    }

    [Fact]
    public void Escapes_a_quote_inside_a_character_default()
    {
        string sql = DdlScripter.ScriptTable(Table(columns:
        [
            Column(1, "note", "VARCHAR(20)", InformixDbType.VarChar, defaultValue: "it's"),
        ]));

        sql.Should().Contain("default 'it''s'");
    }

    [Fact]
    public void Writes_a_foreign_key_with_its_target_owner()
    {
        // Unqualified, the reference resolves against the connected user rather than
        // the table the constraint actually points at.
        string sql = DdlScripter.ScriptTable(Table(constraints:
        [
            new ConstraintDetail
            {
                Name = "fk_orders",
                Owner = "informix",
                Kind = ConstraintKind.ForeignKey,
                Columns = ["order_num"],
                ReferencedTable = "orders",
                ReferencedOwner = "sales",
                RawConstraintType = 'R',
            },
        ]));

        sql.Should().Contain(
            "foreign key (order_num) references \"sales\".orders constraint \"informix\".fk_orders");
    }

    [Fact]
    public void Says_so_when_a_foreign_key_target_could_not_be_read()
    {
        // Half a statement would fail at run time and look like IMS's bug. PR-8.4: name
        // the gap.
        string sql = DdlScripter.ScriptTable(Table(constraints:
        [
            new ConstraintDetail
            {
                Name = "fk_orphan",
                Owner = "informix",
                Kind = ConstraintKind.ForeignKey,
                Columns = ["order_num"],
                RawConstraintType = 'R',
            },
        ]));

        sql.Should().Contain("-- ForeignKey constraint fk_orphan could not be scripted");
        sql.Should().NotContain("references ");
    }

    [Fact]
    public void Writes_a_check_constraint_from_its_stored_text()
    {
        string sql = DdlScripter.ScriptTable(Table(constraints:
        [
            new ConstraintDetail
            {
                Name = "ck_qty",
                Owner = "informix",
                Kind = ConstraintKind.Check,
                CheckExpression = "qty > 0",
                RawConstraintType = 'C',
            },
        ]));

        sql.Should().Contain("check (qty > 0) constraint \"informix\".ck_qty");
    }

    [Fact]
    public void Never_emits_a_not_null_constraint_as_a_table_clause()
    {
        // Informix records NOT NULL in sysconstraints as well as on the column. As a
        // table-level clause it is not SQL any version accepts.
        string sql = DdlScripter.ScriptTable(Table(constraints:
        [
            new ConstraintDetail
            {
                Name = "n100_2",
                Owner = "informix",
                Kind = ConstraintKind.NotNull,
                Columns = ["fname"],
                RawConstraintType = 'N',
            },
        ]));

        sql.Should().NotContain("n100_2");
    }

    [Fact]
    public void Scripts_a_standalone_index_after_the_table()
    {
        string sql = DdlScripter.ScriptTable(Table(indexes:
        [
            new IndexDetail
            {
                Name = "idx_lname",
                Owner = "informix",
                IsUnique = false,
                IsClustered = false,
                Keys = [new IndexKeyColumn("lname", false), new IndexKeyColumn("fname", true)],
            },
        ]));

        sql.Should().Contain(
            "create index \"informix\".idx_lname on \"informix\".customer (lname,fname desc) using btree;");
    }

    [Fact]
    public void Does_not_script_the_index_that_backs_a_constraint()
    {
        // The constraint creates it. Scripting both puts two indexes on one key.
        string sql = DdlScripter.ScriptTable(Table(indexes:
        [
            new IndexDetail
            {
                Name = "u100_1",
                Owner = "informix",
                IsUnique = true,
                IsClustered = false,
                Keys = [new IndexKeyColumn("customer_num", false)],
                BacksConstraint = true,
            },
        ]));

        sql.Should().NotContain("create unique index");
    }

    [Theory]
    [InlineData(true, false, "create unique index")]
    [InlineData(false, true, "create cluster index")]
    [InlineData(true, true, "create unique cluster index")]
    public void Carries_unique_and_cluster_through(bool unique, bool clustered, string expected)
    {
        string sql = DdlScripter.ScriptTable(Table(indexes:
        [
            new IndexDetail
            {
                Name = "i1",
                Owner = "informix",
                IsUnique = unique,
                IsClustered = clustered,
                Keys = [new IndexKeyColumn("c1", false)],
            },
        ]));

        sql.Should().Contain(expected);
    }

    [Fact]
    public void Places_an_unfragmented_table_in_its_dbspace()
    {
        string sql = DdlScripter.ScriptTable(Table(dbSpace: "datadbs"));

        sql.Should().Contain("  ) in datadbs extent size 16 next size 16 lock mode page;");
    }

    [Fact]
    public void Writes_a_round_robin_fragmentation_clause()
    {
        string sql = DdlScripter.ScriptTable(Table(fragments:
        [
            new FragmentDetail { Strategy = "Round robin", RawStrategy = 'R', DbSpace = "dbs1" },
            new FragmentDetail { Strategy = "Round robin", RawStrategy = 'R', DbSpace = "dbs2" },
        ]));

        sql.Should().Contain("fragment by round robin in dbs1, dbs2");
    }

    [Fact]
    public void Writes_an_expression_fragmentation_clause_with_its_remainder()
    {
        string sql = DdlScripter.ScriptTable(Table(fragments:
        [
            new FragmentDetail
            {
                Strategy = "Expression",
                RawStrategy = 'E',
                DbSpace = "dbs1",
                Expression = "id < 100",
            },
            new FragmentDetail { Strategy = "Expression", RawStrategy = 'E', DbSpace = "dbs2" },
        ]));

        sql.Should().Contain("fragment by expression");
        sql.Should().Contain("(id < 100) in dbs1,");
        sql.Should().Contain("remainder in dbs2");
    }

    [Fact]
    public void Reports_an_unrecognised_fragmentation_strategy_rather_than_guessing()
    {
        string sql = DdlScripter.ScriptTable(Table(fragments:
        [
            new FragmentDetail { Strategy = "Unknown ('Z')", RawStrategy = 'Z', DbSpace = "dbs1" },
            new FragmentDetail { Strategy = "Unknown ('Z')", RawStrategy = 'Z', DbSpace = "dbs2" },
        ]));

        sql.Should().Contain("unrecognised strategy 'Z'");
        sql.Should().NotContain("fragment by");
    }

    [Fact]
    public void Omits_the_lock_mode_when_the_catalogue_code_was_not_understood()
    {
        // The server default is the honest fallback; a guess would silently change the
        // table's concurrency behaviour.
        string sql = DdlScripter.ScriptTable(Table(lockMode: "Unknown ('Z')"));

        sql.Should().NotContain("lock mode");
    }

    [Fact]
    public void Names_the_triggers_it_did_not_script()
    {
        string sql = DdlScripter.ScriptTable(Table(triggers:
        [
            new TriggerDetail
            {
                Name = "trg_audit",
                Owner = "informix",
                Event = "INSERT",
                RawEvent = 'I',
            },
        ]));

        sql.Should().Contain("-- Not scripted: 1 trigger — trg_audit (INSERT).");
    }

    [Fact]
    public void Quotes_an_identifier_Informix_would_fold()
    {
        TableDetail detail = Table(columns: [Column(1, "MixedCase", "INTEGER", InformixDbType.Integer)])
            with
        {
            Object = new SchemaObject
            {
                TabId = 100,
                Name = "Order Detail",
                Owner = "informix",
                Kind = SchemaObjectKind.Table,
            },
        };

        string sql = DdlScripter.ScriptTable(detail);

        sql.Should().Contain("create table \"informix\".\"Order Detail\"");
        sql.Should().Contain("\"MixedCase\" integer");
    }

    [Fact]
    public void Says_where_the_script_came_from_and_what_is_missing_from_it()
    {
        // A script that arrives with no provenance is one someone will take for the
        // server's own words. It has to say that privileges are not in it.
        string sql = DdlScripter.ScriptTable(Table());

        sql.Should().StartWith("-- customer — scripted by IMS from the system catalogue.");
        sql.Should().Contain("Privileges are not included.");
    }

    [Fact]
    public void Returns_a_views_own_text_unchanged()
    {
        var view = new SchemaObject
        {
            TabId = 101,
            Name = "vw_active",
            Owner = "informix",
            Kind = SchemaObjectKind.View,
        };

        string sql = DdlScripter.ScriptView(
            view, ["create view \"informix\".vw_active (a) as ", "select a from customer"]);

        sql.Should().Contain("create view \"informix\".vw_active (a) as select a from customer;");
    }

    [Fact]
    public void Does_not_double_a_terminator_the_server_already_supplied()
    {
        var view = new SchemaObject
        {
            TabId = 101,
            Name = "v",
            Owner = "informix",
            Kind = SchemaObjectKind.View,
        };

        DdlScripter.ScriptView(view, ["create view v as select 1 from x;"])
            .Should().NotContain(";;");
    }

    [Fact]
    public void Says_so_when_a_routine_keeps_its_body_outside_the_catalogue()
    {
        var routine = new SchemaObject
        {
            TabId = 5,
            Name = "external_fn",
            Owner = "informix",
            Kind = SchemaObjectKind.Function,
        };

        DdlScripter.ScriptRoutine(routine, [])
            .Should().Contain("The server returned no text");
    }

    [Fact]
    public void Warns_when_a_scripted_index_is_really_a_constraint()
    {
        // Re-running it would create a second index on the same key.
        var table = new SchemaObject
        {
            TabId = 100,
            Name = "customer",
            Owner = "informix",
            Kind = SchemaObjectKind.Table,
        };

        string sql = DdlScripter.ScriptIndex(table, new IndexDetail
        {
            Name = "u100_1",
            Owner = "informix",
            IsUnique = true,
            IsClustered = false,
            Keys = [new IndexKeyColumn("customer_num", false)],
            BacksConstraint = true,
        });

        sql.Should().Contain("exists to enforce a constraint");
    }
}

public class ObjectScripterScopeTests
{
    [Theory]
    [InlineData(SchemaObjectKind.Table)]
    [InlineData(SchemaObjectKind.View)]
    [InlineData(SchemaObjectKind.Index)]
    [InlineData(SchemaObjectKind.Procedure)]
    [InlineData(SchemaObjectKind.Function)]
    public void Scripts_the_kinds_the_tree_offers(SchemaObjectKind kind) =>
        ObjectScripter.CanScript(kind).Should().BeTrue();

    [Theory]
    [InlineData(SchemaObjectKind.Synonym)]
    [InlineData(SchemaObjectKind.Sequence)]
    [InlineData(SchemaObjectKind.UserDefinedType)]
    public void Declines_the_kinds_it_cannot_yet_script(SchemaObjectKind kind) =>
        // The menu item greys out rather than producing an empty tab. PR-2.6 does not
        // cover these and pretending otherwise would be worse than the gap.
        ObjectScripter.CanScript(kind).Should().BeFalse();
}
