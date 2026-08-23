namespace Twig.Domain.Services.Plan;

/// <summary>
/// Read-only status snapshot for a plan file. Aggregates the journal row plus each
/// operation's state so a UI can render current position without additional lookups.
/// <para>
/// The result carries three distinct outcomes:
/// </para>
/// <list type="bullet">
///   <item><b>Input error.</b> The file path, contents, or workspace failed validation
///   before any journal lookup was possible. <see cref="Found"/> is <c>false</c>,
///   <see cref="Issues"/> carries the structured problems, and <see cref="Digest"/> /
///   <see cref="State"/> / <see cref="Operations"/> are unset. The lifecycle service
///   returns this shape rather than <c>null</c> so a caller can distinguish "the file
///   is broken" from "the file has never been previewed".</item>
///   <item><b>Journal loaded.</b> <see cref="Found"/> is <c>true</c> and every other
///   field carries the journal snapshot.</item>
///   <item><b>Valid digest, no journal.</b> The lifecycle service returns <c>null</c>.
///   That is the only case in which <see cref="IPlanLifecycleService.StatusAsync"/>
///   returns <c>null</c>: the file parsed, was inside the workspace, and yielded a
///   digest, but no journal has ever been imported for it.</item>
/// </list>
/// </summary>
public sealed record PlanStatusResult
{
    /// <summary>
    /// Structured input-error issues. Empty when <see cref="Found"/> is true.
    /// </summary>
    public IReadOnlyList<PlanValidationIssue> Issues { get; init; } = [];

    /// <summary>True iff a journal row was loaded for the plan file's digest.</summary>
    public bool Found { get; init; }

    /// <summary>
    /// The plan digest. Populated whenever the parser produced one — that is when the
    /// journal was loaded, and also when the parser succeeded but the workspace guard
    /// or a downstream check rejected the plan afterwards.
    /// </summary>
    public string? Digest { get; init; }

    /// <summary>Top-level plan state from the journal row; null when <see cref="Found"/> is false.</summary>
    public PlanOperationState? State { get; init; }

    /// <summary>Per-operation states in declaration order; empty when <see cref="Found"/> is false.</summary>
    public IReadOnlyList<PlanJournalOperation> Operations { get; init; } = [];

    /// <summary>
    /// Terminal-level error captured on the journal row when apply completed non-Verified.
    /// Distinct from <see cref="Issues"/>, which reports input-level problems raised
    /// before the journal was even consulted.
    /// </summary>
    public string? Error { get; init; }
}
