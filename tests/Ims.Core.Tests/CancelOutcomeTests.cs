using System.Reflection;
using FluentAssertions;
using Ims.Core.Data;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// Pins the shape of the cancellation contract.
/// </summary>
/// <remarks>
/// <para>
/// PR-3.5 asks that a running statement be stopped without losing the session.
/// Measured against 14.10, the ODBC driver's cancel does not reach the
/// server: the statement runs to completion or to its timeout while the session
/// stays usable. IMS therefore stops <em>waiting</em> without stopping the
/// <em>statement</em>.
/// </para>
/// <para>
/// Before this was measured, <c>CancelAsync</c> returned <c>Task</c> and the editor
/// reported "Cancelled" — over a statement still consuming server CPU. These tests
/// exist so that regression cannot happen quietly: a return to a void-shaped API,
/// or the loss of the distinction between the two outcomes, breaks them.
/// </para>
/// </remarks>
public class CancelOutcomeTests
{
    [Fact]
    public void CancelAsync_reports_what_happened_rather_than_returning_nothing()
    {
        MethodInfo cancel = typeof(IInformixSession)
            .GetMethod(nameof(IInformixSession.CancelAsync))!;

        cancel.ReturnType.Should().Be<Task<CancelOutcome>>(
            "a caller cannot tell the user the truth if the provider will not admit "
            + "whether the statement actually stopped (PR-3.5, PR-8.4)");
    }

    [Fact]
    public void Stopping_the_wait_is_distinct_from_stopping_the_statement()
    {
        CancelOutcome.StoppedWaitingOnly.Should().NotBe(
            CancelOutcome.StatementStopped,
            "collapsing these is what let the editor claim a runaway query had been "
            + "cancelled when it was still running");
    }

    [Fact]
    public void Nothing_running_is_not_reported_as_a_stopped_statement()
    {
        CancelOutcome.NothingRunning.Should().NotBe(CancelOutcome.StatementStopped);
        CancelOutcome.NothingRunning.Should().NotBe(CancelOutcome.StoppedWaitingOnly);
    }

    /// <summary>
    /// The success case stays in the enum although no driver currently produces it.
    /// </summary>
    /// <remarks>
    /// It is what PR-3.5 actually asks for, and a CSDK or server upgrade could make it
    /// reachable — the smoke test's <c>--recheck-cancellation</c> is how that would be
    /// noticed. Deleting it as dead code would erase the requirement from the model.
    /// </remarks>
    [Fact]
    public void The_outcome_PR_3_5_asks_for_is_still_expressible()
    {
        Enum.IsDefined(CancelOutcome.StatementStopped).Should().BeTrue();
    }
}
