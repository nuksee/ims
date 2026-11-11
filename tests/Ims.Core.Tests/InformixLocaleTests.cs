using FluentAssertions;
using Ims.Core.Connections;
using Xunit;

namespace Ims.Core.Tests;

public class InformixLocaleTests
{
    [Theory]
    [InlineData("en_US.819")]
    [InlineData("en_US.8859-1")]
    [InlineData("en_US.CP1252")]
    [InlineData("en_us.utf8")]
    [InlineData("en_US.57372")]
    [InlineData("ru_RU.CP1251")]
    [InlineData("de_DE.8859-1@euro")]
    public void Accepts_well_formed_locale_names(string value) =>
        InformixLocale.IsWellFormed(value).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("en_US")]
    [InlineData("en_US.")]
    [InlineData("enUS.819")]
    [InlineData("en_US.819;Pwd=x")]
    [InlineData("en_US .819")]
    [InlineData("UTF-8")]
    public void Rejects_malformed_locale_names(string? value) =>
        InformixLocale.IsWellFormed(value).Should().BeFalse();

    [Fact]
    public void Every_suggestion_is_itself_well_formed() =>
        InformixLocale.CommonLocales.Should().OnlyContain(l => InformixLocale.IsWellFormed(l));
}
