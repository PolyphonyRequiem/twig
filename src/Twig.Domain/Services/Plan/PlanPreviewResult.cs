using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// The preview surface consumers see. Carries the digest so a user can compare against
/// the source file, plus the validated operation list so a UI can render "what will be
/// done" without re-parsing. Issues is empty for a valid preview.
/// </summary>
public sealed record PlanPreviewResult
{
    /// <summary>Canonical digest; null iff parse failed.</summary>
    public string? Digest { get; init; }

    /// <summary>Validated operations, or empty when the plan is invalid.</summary>
    public required IReadOnlyList<PlanOperationDefinition> Operations { get; init; }

    /// <summary>Every parser issue — the same list the parser produced.</summary>
    public required IReadOnlyList<PlanValidationIssue> Issues { get; init; }

    /// <summary>The parsed workspace; null when the parse never reached it.</summary>
    public PlanWorkspace? Workspace { get; init; }

    /// <summary>
    /// Exact snapshot of every currently-staged pending change at preview time. Preview never
    /// mutates the journal, so this is the raw row order the store returned. A non-empty list
    /// blocks apply — <see cref="CanApply"/> is false — because plan v1 is declarative-only
    /// and never auto-flushes pending edits.
    /// </summary>
    public required IReadOnlyList<PendingChangeDetail> PendingChanges { get; init; }

    /// <summary>
    /// True iff the plan is valid, its workspace matches the active config, no pending row
    /// exists, and the journal has been imported successfully. False otherwise.
    /// </summary>
    public required bool CanApply { get; init; }
}
