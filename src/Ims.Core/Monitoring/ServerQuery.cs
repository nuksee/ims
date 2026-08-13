namespace Ims.Core.Monitoring;

/// <summary>
/// One query IMS sent, with the CLI command that answers the same question.
/// </summary>
/// <remarks>
/// <para>
/// PR-8.2 requires the raw query on demand and PR-8.3 requires IMS to teach the platform
/// by naming the equivalent command. Carrying both alongside the data makes each
/// structural rather than something the UI reconstructs — and it means the command shown
/// is attached to the query that actually ran, so the pair cannot drift.
/// </para>
/// <para>
/// This is the session monitor's counterpart to <see cref="Catalog.CatalogResult{T}"/>.
/// A catalogue read is one query returning one list; a session read is several
/// independently-failing sub-reads accumulating into one pane, in the shape
/// <c>GetTableDetailAsync</c> established. So the query travels in a list beside the
/// data rather than paired with it, and it carries an outcome.
/// </para>
/// <para>
/// <see cref="Outcome"/> is here because a section that failed is part of what IMS
/// asked. Hiding the query that did not work would leave the user looking at an empty
/// section with no way to find out why — which is the opaque failure NFR-4 rules out.
/// </para>
/// </remarks>
/// <param name="Purpose">What this query was for, in the user's terms.</param>
/// <param name="Sql">The statement as sent, verbatim (PR-8.2, PR-6.2).</param>
/// <param name="OnstatEquivalent">
/// The <c>onstat</c> command answering the same question, from the PRD's parity map —
/// <c>onstat -g ses</c>, <c>-g sql</c>, <c>-g lok</c>. Empty where there is no
/// equivalent; never invented, because PR-8.3's value is that the command named is one
/// that actually works.
/// </param>
/// <param name="Outcome">Whether the server answered.</param>
/// <param name="Message">Why it did not, where it did not.</param>
public sealed record ServerQuery(
    string Purpose,
    string Sql,
    string OnstatEquivalent,
    ServerQueryOutcome Outcome = ServerQueryOutcome.Succeeded,
    string? Message = null);

/// <summary>What became of a query IMS sent.</summary>
public enum ServerQueryOutcome
{
    Succeeded,

    /// <summary>The server refused it — a missing catalogue object, or no privilege.</summary>
    Failed,

    /// <summary>Not sent, because something it depended on had already failed.</summary>
    NotAttempted,
}
