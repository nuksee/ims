using FluentAssertions;
using Ims.Core.Catalog;
using Ims.Core.Data;
using Ims.Data.Informix.Catalog;
using Xunit;

namespace Ims.Data.Informix.Tests;

public class CatalogQueryCompositionTests
{
    [Fact]
    public void Excludes_the_system_catalogue_by_default()
    {
        // tabid 1-99 is Informix's own catalogue. It is not what a developer means
        // by "the tables in this database", and it would bury a small schema.
        string sql = CatalogQueries.ObjectsByType(includeSystem: false, null, null);

        sql.Should().Contain("tabid > 99");
    }

    [Fact]
    public void Includes_the_system_catalogue_when_asked()
    {
        CatalogQueries.ObjectsByType(includeSystem: true, null, null)
            .Should().NotContain("tabid > 99");
    }

    [Fact]
    public void Adds_a_predicate_per_filter_and_no_more()
    {
        // The parameters are positional, so the query's shape and the parameter list
        // have to agree exactly. A stray predicate misaligns everything after it.
        CatalogQueries.ObjectsByType(false, null, null).Should().NotContain("LIKE");
        CatalogQueries.ObjectsByType(false, "%cust%", null).Should().Contain("LIKE ?");
        CatalogQueries.ObjectsByType(false, null, "informix").Should().Contain("owner = ?");

        string both = CatalogQueries.ObjectsByType(false, "%cust%", "informix");

        both.Should().Contain("LIKE ?");
        both.Should().Contain("owner = ?");
    }

    [Fact]
    public void Keeps_ORDER_BY_last()
    {
        // Composing predicates by appending is only safe if ORDER BY stays at the end.
        string sql = CatalogQueries.ObjectsByType(false, "%cust%", "informix");

        sql.TrimEnd().Should().EndWith("ORDER BY owner, tabname");
    }

    [Fact]
    public void Routines_exclude_the_informix_owner_unless_system_is_wanted()
    {
        CatalogQueries.Routines(false, null, null).Should().Contain("owner <> 'informix'");
        CatalogQueries.Routines(true, null, null).Should().NotContain("owner <> 'informix'");
    }

    [Fact]
    public void Index_queries_use_sysindexes_not_sysindices()
    {
        // sysindexes is the compatibility view exposing part1..part16 as columns;
        // sysindices stores the key as a composite type that ODBC cannot read.
        CatalogQueries.Indexes.Should().Contain("sysindexes");
        CatalogQueries.Indexes.Should().Contain("part16");
        CatalogQueries.AllIndexes(false, null).Should().Contain("sysindexes");
    }

    [Fact]
    public void A_views_text_comes_back_in_the_order_the_server_stored_it()
    {
        // sysviews splits the CREATE VIEW statement across numbered rows; concatenating
        // them out of order would produce syntactically valid nonsense (PR-2.6).
        CatalogQueries.ViewSource.Should().Contain("sysviews");
        CatalogQueries.ViewSource.Should().Contain("tabid = ?");
        CatalogQueries.ViewSource.TrimEnd().Should().EndWith("ORDER BY seqno");
    }

    [Fact]
    public void Table_detail_queries_are_all_keyed_on_tabid()
    {
        // PR-6.4: negligible on a production instance. Every per-table query has to
        // be a keyed lookup, never a scan.
        foreach (string sql in (string[])
                 [
                     CatalogQueries.TableRow,
                     CatalogQueries.Columns,
                     CatalogQueries.Indexes,
                     CatalogQueries.Constraints,
                     CatalogQueries.Triggers,
                     CatalogQueries.Fragments,
                 ])
        {
            sql.Should().Contain("tabid = ?");
        }
    }
}

public class CatalogTranslationTests
{
    [Theory]
    [InlineData(SchemaObjectKind.Table, "T")]
    [InlineData(SchemaObjectKind.View, "V")]
    [InlineData(SchemaObjectKind.Synonym, "S")]
    [InlineData(SchemaObjectKind.PrivateSynonym, "P")]
    [InlineData(SchemaObjectKind.Sequence, "Q")]
    public void Maps_an_object_kind_to_its_tabtype(SchemaObjectKind kind, string expected)
    {
        InformixCatalogReader.TabTypeFor(kind).Should().Be(expected);
    }

    [Theory]
    [InlineData('R', "Row")]
    [InlineData('T', "Table")]
    [InlineData('B', "Page")]
    [InlineData('P', "Page")]
    public void Describes_a_lock_level(char raw, string expected)
    {
        InformixCatalogReader.DescribeLockLevel(raw).Should().Be(expected);
    }

    [Fact]
    public void An_unrecognised_lock_level_shows_the_raw_character()
    {
        // PR-8.2: never hide the server. An unknown code is reported, not guessed at.
        InformixCatalogReader.DescribeLockLevel('Z').Should().Contain("Z");
    }

    [Theory]
    [InlineData('P', ConstraintKind.PrimaryKey)]
    [InlineData('U', ConstraintKind.Unique)]
    [InlineData('R', ConstraintKind.ForeignKey)]
    [InlineData('C', ConstraintKind.Check)]
    [InlineData('N', ConstraintKind.NotNull)]
    [InlineData('?', ConstraintKind.Other)]
    public void Maps_a_constraint_type(char raw, ConstraintKind expected)
    {
        InformixCatalogReader.DescribeConstraint(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData('I', "INSERT")]
    [InlineData('U', "UPDATE")]
    [InlineData('D', "DELETE")]
    [InlineData('S', "SELECT")]
    public void Maps_a_trigger_event(char raw, string expected)
    {
        InformixCatalogReader.DescribeTriggerEvent(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData('R', "Round robin")]
    [InlineData('E', "Expression")]
    [InlineData('H', "Hash")]
    [InlineData('I', "Interval")]
    [InlineData('L', "List")]
    public void Maps_a_fragmentation_strategy(char raw, string expected)
    {
        InformixCatalogReader.DescribeFragmentStrategy(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false, false, DatabaseLogging.None)]
    [InlineData(true, true, false, DatabaseLogging.Buffered)]
    [InlineData(true, false, false, DatabaseLogging.Unbuffered)]
    [InlineData(true, false, true, DatabaseLogging.Ansi)]
    public void Maps_the_logging_mode(bool logging, bool buffered, bool ansi, DatabaseLogging expected)
    {
        // PR-3.7 hangs off this: an unlogged database has no transactions to show.
        InformixCatalogReader.DescribeLogging(logging, buffered, ansi).Should().Be(expected);
    }

    [Theory]
    [InlineData('C', "CURRENT")]
    [InlineData('T', "TODAY")]
    [InlineData('U', "USER")]
    [InlineData('N', "NULL")]
    public void Describes_a_non_literal_default(char type, string expected)
    {
        InformixCatalogReader.DescribeDefault(type, null, InformixDbType.Integer)
            .Should().Be(expected);
    }

    [Fact]
    public void Strips_the_encoding_Informix_puts_before_a_numeric_default()
    {
        // Observed against 14.10: an INTEGER defaulting to 0 is stored as "AAAAAA 0".
        // The pane showed the raw value, which is the opposite of what PR-2.4 is for.
        InformixCatalogReader.DescribeDefault('L', "AAAAAA 0", InformixDbType.Integer)
            .Should().Be("0");
    }

    [Theory]
    [InlineData(InformixDbType.SmallInt, "AAAAAA 1", "1")]
    [InlineData(InformixDbType.Decimal, "AAAAAAAAAA 3.14", "3.14")]
    [InlineData(InformixDbType.Money, "AAAAAAAAAA 0.00", "0.00")]
    public void Strips_the_encoding_for_every_non_character_type(
        InformixDbType dbType,
        string stored,
        string expected)
    {
        InformixCatalogReader.DescribeDefault('L', stored, dbType).Should().Be(expected);
    }

    [Fact]
    public void A_datetime_default_keeps_the_spaces_inside_its_literal()
    {
        // Only the first space separates the encoding from the literal; the literal
        // itself may contain more.
        InformixCatalogReader.DescribeDefault(
            'L', "AAAAAA 2026-08-06 11:22:33", InformixDbType.DateTime)
            .Should().Be("2026-08-06 11:22:33");
    }

    [Theory]
    [InlineData(InformixDbType.Char, "informix")]
    [InlineData(InformixDbType.VarChar, "not applicable")]
    [InlineData(InformixDbType.LVarChar, "two words")]
    public void A_character_default_is_stored_whole_and_left_alone(
        InformixDbType dbType,
        string stored)
    {
        // The whole value is the default here, spaces and all — splitting on the
        // first space would silently truncate it.
        InformixCatalogReader.DescribeDefault('L', stored, dbType).Should().Be(stored);
    }

    [Fact]
    public void A_numeric_default_with_no_encoding_survives_unchanged()
    {
        // BOOLEAN defaults arrive as a bare 'f' with no prefix.
        InformixCatalogReader.DescribeDefault('L', "f", InformixDbType.Boolean).Should().Be("f");
    }

    [Fact]
    public void An_empty_default_is_empty_rather_than_null()
    {
        InformixCatalogReader.DescribeDefault('L', null, InformixDbType.Integer)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_negative_index_part_is_a_descending_column()
    {
        // sysindexes encodes direction in the sign of the part number. Scripting needs
        // the direction and the name apart — CREATE INDEX wants "col desc", a primary
        // key clause wants the bare column (PR-2.6).
        IReadOnlyList<ColumnDetail> columns =
        [
            new()
            {
                Position = 2,
                Name = "lname",
                DbType = InformixDbType.Char,
                TypeDescription = "CHAR(15)",
                IsNullable = true,
                RawColType = 0,
                RawColLength = 15,
            },
        ];

        InformixCatalogReader.KeyForPart(2, columns).Should().Be(new IndexKeyColumn("lname", false));
        InformixCatalogReader.KeyForPart(-2, columns).Should().Be(new IndexKeyColumn("lname", true));
    }

    [Fact]
    public void An_index_part_naming_a_column_that_was_not_read_says_so()
    {
        // PR-8.4: the tree shows what the catalogue said, not an invented name.
        InformixCatalogReader.KeyForPart(7, []).Name.Should().Be("(column 7)");
    }

    [Theory]
    // The catalogue packs DECIMAL as (precision * 256) + scale.
    [InlineData(InformixDbType.Decimal, (10 * 256) + 2, 10, 2)]
    [InlineData(InformixDbType.Money, (16 * 256) + 2, 16, 2)]
    public void Decodes_decimal_precision_and_scale(
        InformixDbType dbType,
        int collength,
        int expectedPrecision,
        int expectedScale)
    {
        (int? precision, int? scale) = InformixCatalogReader.DecodeDecimal(dbType, collength);

        precision.Should().Be(expectedPrecision);
        scale.Should().Be(expectedScale);
    }

    [Fact]
    public void Only_decimal_types_carry_precision_and_scale()
    {
        InformixCatalogReader.DecodeDecimal(InformixDbType.Integer, 4)
            .Should().Be((null, null));
    }

    [Fact]
    public void Describes_a_datetime_with_its_qualifier()
    {
        InformixCatalogReader.DescribeType(
            InformixDbType.DateTime, 4365, DateTimeQualifier.YearToFraction3, null, null)
            .Should().Be("DATETIME YEAR TO FRACTION(3)");
    }

    [Fact]
    public void Describes_a_decimal_the_way_a_user_would_write_it()
    {
        InformixCatalogReader.DescribeType(InformixDbType.Decimal, (10 * 256) + 2, null, 10, 2)
            .Should().Be("DECIMAL(10,2)");
    }

    [Theory]
    [InlineData(InformixDbType.Char, 30, "CHAR(30)")]
    [InlineData(InformixDbType.VarChar, 255, "VARCHAR(255)")]
    public void Describes_a_character_type_with_its_length(
        InformixDbType dbType,
        int collength,
        string expected)
    {
        InformixCatalogReader.DescribeType(dbType, collength, null, null, null).Should().Be(expected);
    }
}

public class SchemaObjectTests
{
    [Theory]
    [InlineData("customer", "customer")]
    [InlineData("sales_order", "sales_order")]
    [InlineData("_private", "_private")]
    public void Leaves_a_safe_identifier_unquoted(string name, string expected)
    {
        SchemaObject.Quote(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("MixedCase")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("1leading")]
    public void Quotes_an_identifier_Informix_would_not_read_back(string name)
    {
        // Informix folds unquoted identifiers to lower case, so generated SQL that
        // does not delimit a mixed-case name will not find the object it names.
        SchemaObject.Quote(name).Should().StartWith("\"").And.EndWith("\"");
    }

    [Fact]
    public void Escapes_an_embedded_quote()
    {
        SchemaObject.Quote("odd\"name").Should().Be("\"odd\"\"name\"");
    }

    [Fact]
    public void Qualifies_a_name_with_its_owner()
    {
        var table = new SchemaObject
        {
            TabId = 100,
            Name = "sales_order",
            Owner = "informix",
            Kind = SchemaObjectKind.Table,
        };

        table.QualifiedName.Should().Be("informix.sales_order");
    }
}
