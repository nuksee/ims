namespace Ims.SmokeTest;

/// <summary>How a probe turned out.</summary>
public enum ProbeOutcome
{
    Pass,
    Fail,

    /// <summary>Not run — a prerequisite probe failed, or it needs a flag not given.</summary>
    Skipped,

    /// <summary>Ran, but the answer needs a human to interpret it.</summary>
    Inconclusive,
}

/// <summary>
/// One question the spike set out to answer, and what the server said.
/// </summary>
public sealed record ProbeResult
{
    public required string Name { get; init; }

    /// <summary>The PRD requirement or question this probe exists to settle.</summary>
    public required string Requirement { get; init; }

    public required ProbeOutcome Outcome { get; init; }

    /// <summary>What was observed, in a form a human can act on.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// The statement sent, where one was. Printed so the run is auditable against
    /// PR-6.2 — IMS sends nothing the user did not ask for.
    /// </summary>
    public string? Statement { get; init; }

    public static ProbeResult Pass(string name, string requirement, string detail, string? statement = null) =>
        new() { Name = name, Requirement = requirement, Outcome = ProbeOutcome.Pass, Detail = detail, Statement = statement };

    public static ProbeResult Fail(string name, string requirement, string detail, string? statement = null) =>
        new() { Name = name, Requirement = requirement, Outcome = ProbeOutcome.Fail, Detail = detail, Statement = statement };

    public static ProbeResult Skip(string name, string requirement, string detail) =>
        new() { Name = name, Requirement = requirement, Outcome = ProbeOutcome.Skipped, Detail = detail };

    public static ProbeResult Inconclusive(string name, string requirement, string detail, string? statement = null) =>
        new() { Name = name, Requirement = requirement, Outcome = ProbeOutcome.Inconclusive, Detail = detail, Statement = statement };
}
