using FluentAssertions;
using Ims.Core.Data;
using Xunit;

namespace Ims.Core.Tests;

public class InformixValueTests
{
    [Fact]
    public void Null_empty_string_and_zero_are_all_distinguishable()
    {
        // PR-4.4. The usual failure is that a null becomes DBNull or "" somewhere in
        // the pipeline and the distinction can no longer be recovered at the grid.
        var nullValue = InformixValue.Null(InformixDbType.VarChar);
        var emptyString = InformixValue.From(InformixDbType.VarChar, string.Empty);
        var zero = InformixValue.From(InformixDbType.Integer, 0);

        nullValue.IsNull.Should().BeTrue();
        emptyString.IsNull.Should().BeFalse();
        zero.IsNull.Should().BeFalse();

        nullValue.Should().NotBe(emptyString);
        emptyString.Should().NotBe(zero);

        nullValue.ToDisplayString().Should().Be("(null)");
        emptyString.ToDisplayString().Should().BeEmpty();
        zero.ToDisplayString().Should().Be("0");
    }

    [Fact]
    public void A_null_keeps_its_column_type()
    {
        // The grid still needs to know what kind of null it is holding, for alignment
        // and for the export path.
        InformixValue.Null(InformixDbType.Money).DbType.Should().Be(InformixDbType.Money);
    }

    [Fact]
    public void DBNull_is_normalised_to_a_typed_null()
    {
        InformixValue value = InformixValue.From(InformixDbType.Integer, DBNull.Value);

        value.IsNull.Should().BeTrue();
        value.Value.Should().BeNull();
    }

    [Fact]
    public void Export_can_render_null_as_an_empty_field()
    {
        // A literal "NULL" in a CSV is a value, not an absence, so the caller chooses.
        InformixValue.Null(InformixDbType.VarChar)
            .ToDisplayString(nullRepresentation: string.Empty)
            .Should().BeEmpty();
    }

    [Fact]
    public void Boolean_renders_using_Informix_literals()
    {
        InformixValue.From(InformixDbType.Boolean, true).ToDisplayString().Should().Be("t");
        InformixValue.From(InformixDbType.Boolean, false).ToDisplayString().Should().Be("f");
    }

    [Fact]
    public void A_large_object_shows_a_placeholder_rather_than_its_bytes()
    {
        // PR-4.5: "a viewable value, not raw bytes in a cell".
        var reference = new LargeObjectReference(
            InformixDbType.Blob,
            sizeInBytes: 2048,
            fetch: _ => Task.FromResult(ReadOnlyMemory<byte>.Empty));

        InformixValue value = InformixValue.LargeObject(reference);

        value.ToDisplayString().Should().Be("<BLOB, 2 KB>");
        value.TryGetLargeObject(out LargeObjectReference? recovered).Should().BeTrue();
        recovered.Should().BeSameAs(reference);
    }

    [Fact]
    public void A_large_object_is_not_fetched_until_it_is_opened()
    {
        // PR-4.2 and PR-6.2 together: the round trip happens only when the user asks.
        var fetched = false;

        var reference = new LargeObjectReference(
            InformixDbType.Text,
            sizeInBytes: null,
            fetch: _ =>
            {
                fetched = true;
                return Task.FromResult(ReadOnlyMemory<byte>.Empty);
            });

        InformixValue value = InformixValue.LargeObject(reference);
        _ = value.ToDisplayString();

        fetched.Should().BeFalse("rendering a cell must not pull the object from the server");
    }

    [Fact]
    public void A_datetime_keeps_the_qualifier_it_was_read_with()
    {
        InformixValue value = InformixValue.DateTime(
            new DateTime(2026, 8, 5, 9, 15, 0, DateTimeKind.Unspecified),
            DateTimeQualifier.YearToDay);

        value.TryGetDateTime(out QualifiedDateTime qualified).Should().BeTrue();
        qualified.Qualifier.Should().Be(DateTimeQualifier.YearToDay);
        value.ToDisplayString().Should().Be("2026-08-05");
    }
}
