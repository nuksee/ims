using FluentAssertions;
using Ims.Core.Data;
using Xunit;

namespace Ims.Data.Informix.Tests;

public class InformixTypeMapperTests
{
    // ---- Catalogue qualifier encoding -----------------------------------------

    [Fact]
    public void Decodes_the_well_known_YEAR_TO_SECOND_collength()
    {
        // 3594 is the value Informix stores for DATETIME YEAR TO SECOND:
        // (14 digits * 256) + (YEAR=0 * 16) + SECOND=10.
        DateTimeQualifier qualifier = InformixTypeMapper.DecodeCatalogQualifier(3594);

        qualifier.Start.Should().Be(DateTimeField.Year);
        qualifier.End.Should().Be(DateTimeField.Second);
        qualifier.ToString().Should().Be("YEAR TO SECOND");
    }

    [Fact]
    public void Decodes_the_well_known_YEAR_TO_FRACTION3_collength()
    {
        // 4365 = (17 digits * 256) + (YEAR=0 * 16) + FRACTION(3)=13.
        DateTimeQualifier qualifier = InformixTypeMapper.DecodeCatalogQualifier(4365);

        qualifier.ToString().Should().Be("YEAR TO FRACTION(3)");
        qualifier.FractionalDigits.Should().Be(3);
    }

    [Theory]
    [InlineData(DateTimeField.Year, DateTimeField.Second, 3594)]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction3, 4365)]
    public void Encodes_a_qualifier_the_way_the_catalogue_stores_it(
        DateTimeField start,
        DateTimeField end,
        int expected)
    {
        InformixTypeMapper.EncodeCatalogQualifier(new DateTimeQualifier(start, end))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(DateTimeField.Year, DateTimeField.Second)]
    [InlineData(DateTimeField.Year, DateTimeField.Day)]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction5)]
    [InlineData(DateTimeField.Hour, DateTimeField.Second)]
    [InlineData(DateTimeField.Day, DateTimeField.Minute)]
    [InlineData(DateTimeField.Month, DateTimeField.Month)]
    public void Round_trips_every_qualifier_through_the_catalogue_encoding(
        DateTimeField start,
        DateTimeField end)
    {
        var original = new DateTimeQualifier(start, end);

        int encoded = InformixTypeMapper.EncodeCatalogQualifier(original);

        InformixTypeMapper.DecodeCatalogQualifier(encoded).Should().Be(original);
    }

    [Theory]
    [InlineData(DateTimeField.Year, DateTimeField.Second, 14)]
    [InlineData(DateTimeField.Year, DateTimeField.Fraction3, 17)]
    [InlineData(DateTimeField.Year, DateTimeField.Day, 8)]
    [InlineData(DateTimeField.Hour, DateTimeField.Second, 6)]
    public void Counts_the_digits_a_qualifier_spans(
        DateTimeField start,
        DateTimeField end,
        int expected)
    {
        InformixTypeMapper.DigitsFor(new DateTimeQualifier(start, end)).Should().Be(expected);
    }

    [Fact]
    public void Rejects_a_collength_that_is_not_a_qualifier()
    {
        // Time unit 1 does not exist — Informix numbers them 0, 2, 4, 6, 8, 10, 11-15.
        Action act = () => InformixTypeMapper.DecodeCatalogQualifier((14 * 256) + (1 * 16) + 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- Written qualifiers ----------------------------------------------------

    [Theory]
    [InlineData("YEAR TO SECOND", "YEAR TO SECOND")]
    [InlineData("DATETIME YEAR TO FRACTION(3)", "YEAR TO FRACTION(3)")]
    [InlineData("INTERVAL DAY TO SECOND", "DAY TO SECOND")]
    [InlineData("year to day", "YEAR TO DAY")]
    [InlineData("MONTH", "MONTH")]
    public void Parses_a_written_qualifier(string input, string expected)
    {
        InformixTypeMapper.TryParseQualifier(input, out DateTimeQualifier qualifier)
            .Should().BeTrue();

        qualifier.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SECOND TO YEAR")]      // inverted
    [InlineData("YEAR TO PARSEC")]
    public void Rejects_an_unparseable_qualifier(string? input)
    {
        InformixTypeMapper.TryParseQualifier(input, out _).Should().BeFalse();
    }

    // ---- Catalogue type codes ---------------------------------------------------

    [Theory]
    [InlineData(0, InformixDbType.Char)]
    [InlineData(2, InformixDbType.Integer)]
    [InlineData(5, InformixDbType.Decimal)]
    [InlineData(6, InformixDbType.Serial)]
    [InlineData(8, InformixDbType.Money)]
    [InlineData(10, InformixDbType.DateTime)]
    [InlineData(13, InformixDbType.VarChar)]
    [InlineData(14, InformixDbType.Interval)]
    [InlineData(43, InformixDbType.LVarChar)]
    [InlineData(45, InformixDbType.Boolean)]
    [InlineData(52, InformixDbType.BigInt)]
    [InlineData(53, InformixDbType.BigSerial)]
    public void Maps_a_catalogue_type_code(int coltype, InformixDbType expected)
    {
        InformixTypeMapper.FromCatalogTypeCode(coltype).Should().Be(expected);
    }

    [Fact]
    public void Recovers_the_NOT_NULL_flag_the_catalogue_adds()
    {
        // syscolumns adds 256 to coltype for a NOT NULL column.
        const int notNullInteger = 2 + 256;

        InformixTypeMapper.FromCatalogTypeCode(notNullInteger).Should().Be(InformixDbType.Integer);
        InformixTypeMapper.IsNotNullFromCatalog(notNullInteger).Should().BeTrue();
        InformixTypeMapper.IsNotNullFromCatalog(2).Should().BeFalse();
    }

    [Theory]
    [InlineData(40)]
    [InlineData(41)]
    public void Flags_opaque_codes_as_needing_an_extended_type_lookup(int coltype)
    {
        // 40 and 41 cover BLOB, CLOB, BOOLEAN and every UDT. Saying "Other" is honest;
        // guessing would violate PR-8.4.
        InformixTypeMapper.RequiresExtendedTypeLookup(coltype).Should().BeTrue();
        InformixTypeMapper.FromCatalogTypeCode(coltype).Should().Be(InformixDbType.Other);
    }

    // ---- Type names -------------------------------------------------------------

    [Theory]
    [InlineData("INTEGER", InformixDbType.Integer)]
    [InlineData("VARCHAR(255)", InformixDbType.VarChar)]
    [InlineData("DECIMAL(10,2)", InformixDbType.Decimal)]
    [InlineData("MONEY(16,2)", InformixDbType.Money)]
    [InlineData("DATETIME YEAR TO SECOND", InformixDbType.DateTime)]
    [InlineData("INTERVAL DAY TO SECOND", InformixDbType.Interval)]
    [InlineData("boolean", InformixDbType.Boolean)]
    [InlineData("LVARCHAR", InformixDbType.LVarChar)]
    [InlineData("SOMETHING_ODD", InformixDbType.Other)]
    [InlineData(null, InformixDbType.Unknown)]
    public void Maps_a_server_type_name(string? typeName, InformixDbType expected)
    {
        InformixTypeMapper.FromServerTypeName(typeName).Should().Be(expected);
    }

    // ---- Value mapping ----------------------------------------------------------

    private static ResultColumn Column(
        InformixDbType type,
        DateTimeQualifier? qualifier = null) => new()
        {
            Ordinal = 0,
            Name = "c",
            DbType = type,
            ServerTypeName = type.ToString(),
            Qualifier = qualifier,
        };

    [Fact]
    public void Maps_a_database_null_to_a_typed_null()
    {
        InformixValue value = InformixTypeMapper.ToInformixValue(
            Column(InformixDbType.VarChar),
            DBNull.Value);

        value.IsNull.Should().BeTrue();
        value.DbType.Should().Be(InformixDbType.VarChar);
    }

    [Fact]
    public void Attaches_the_column_qualifier_to_a_datetime()
    {
        InformixValue value = InformixTypeMapper.ToInformixValue(
            Column(InformixDbType.DateTime, DateTimeQualifier.YearToDay),
            new DateTime(2026, 8, 5, 11, 22, 33, DateTimeKind.Unspecified));

        value.TryGetDateTime(out QualifiedDateTime qualified).Should().BeTrue();
        qualified.Qualifier.Should().Be(DateTimeQualifier.YearToDay);
        value.ToDisplayString().Should().Be("2026-08-05");
    }

    [Fact]
    public void Parses_an_interval_that_arrived_as_text()
    {
        var qualifier = new DateTimeQualifier(DateTimeField.Day, DateTimeField.Second);

        InformixValue value = InformixTypeMapper.ToInformixValue(
            Column(InformixDbType.Interval, qualifier),
            "5 12:30:45");

        value.TryGetInterval(out InformixInterval interval).Should().BeTrue();
        interval.Days.Should().Be(5);
        value.ToDisplayString().Should().Be("5 12:30:45");
    }

    [Theory]
    [InlineData("t", true)]
    [InlineData("f", false)]
    [InlineData("T", true)]
    public void Maps_the_character_form_of_BOOLEAN(string raw, bool expected)
    {
        InformixValue value = InformixTypeMapper.ToInformixValue(Column(InformixDbType.Boolean), raw);

        value.Value.Should().Be(expected);
    }

    [Fact]
    public void Keeps_an_unrecognised_value_rather_than_discarding_it()
    {
        // PR-8.4: if IMS cannot interpret something, it must not silently drop it.
        InformixValue value = InformixTypeMapper.ToInformixValue(
            Column(InformixDbType.Interval),
            "definitely not an interval");

        value.IsNull.Should().BeFalse();
        value.Value.Should().Be("definitely not an interval");
    }
}
