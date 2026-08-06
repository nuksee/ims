using FluentAssertions;
using Xunit;

namespace Ims.Data.Informix.Tests;

public class InformixErrorTranslatorTests
{
    [Theory]
    [InlineData(-201)]
    [InlineData(-206)]
    [InlineData(-217)]
    [InlineData(-239)]
    [InlineData(-268)]
    [InlineData(-284)]
    [InlineData(-310)]
    [InlineData(-329)]
    [InlineData(-349)]
    [InlineData(-391)]
    [InlineData(-692)]
    [InlineData(-908)]
    [InlineData(-951)]
    [InlineData(-1213)]
    public void Explains_the_codes_a_user_actually_hits(int sqlCode)
    {
        InformixErrorTranslator.Explain(sqlCode).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Says_nothing_rather_than_inventing_an_explanation()
    {
        // PR-8.4: a half-implemented capability is worse than an absent one. The
        // server's own message is shown either way (PR-8.2).
        InformixErrorTranslator.Explain(-999999).Should().BeNull();
        InformixErrorTranslator.Explain(0).Should().BeNull();
    }

    [Fact]
    public void The_ISAM_explanation_wins_because_it_is_the_specific_one()
    {
        // On Informix the ISAM error is very often the one that says what actually
        // went wrong; the SQLCODE is frequently just "could not insert".
        string? explanation = InformixErrorTranslator.Explain(sqlCode: -271, isamCode: -107);

        explanation.Should().Contain("lock");
    }

    [Theory]
    [InlineData(-107, "lock")]
    [InlineData(-113, "lock")]
    [InlineData(-143, "deadlock")]
    [InlineData(-144, "timed out")]
    public void Explains_the_common_ISAM_errors(int isamCode, string expected)
    {
        InformixErrorTranslator.Explain(0, isamCode)
            .Should().NotBeNull()
            .And.Subject.As<string>().ToLowerInvariant().Should().Contain(expected);
    }

    [Theory]
    [InlineData(-908, null)]
    [InlineData(-930, null)]
    [InlineData(-25580, null)]
    [InlineData(0, "08S01")]
    [InlineData(0, "08003")]
    public void Recognises_a_lost_connection(int sqlCode, string? sqlState)
    {
        // PR-1.7: this is what distinguishes "reconnect and keep the editor" from
        // "your statement was wrong".
        InformixErrorTranslator.IsConnectionLost(sqlCode, sqlState).Should().BeTrue();
    }

    [Theory]
    [InlineData(-201, "42000")]
    [InlineData(-206, null)]
    public void Does_not_mistake_a_statement_error_for_a_lost_connection(int sqlCode, string? sqlState)
    {
        InformixErrorTranslator.IsConnectionLost(sqlCode, sqlState).Should().BeFalse();
    }

    [Fact]
    public void An_explanation_reads_as_advice_not_as_a_restatement()
    {
        // The value of PR-3.6 is the sentence that tells the user what to do next.
        string? explanation = InformixErrorTranslator.Explain(-206);

        explanation.Should().Contain("does not exist");
        explanation.Should().Contain("connected to the database you meant");
    }
}
