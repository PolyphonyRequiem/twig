namespace Twig.Domain.Services.Plan;

/// <summary>
/// A validated plan document. Immutable declarative input — carries no lifecycle
/// state. The canonical digest of the original bytes is the only key the journal uses to
/// bind execution state back to a source file.
/// </summary>
public sealed record PlanDefinition
{
    /// <summary>Plan schema version. Version 1 retains integer ADO identities; version 2 binds provider identities.</summary>
    public required int Version { get; init; }

    /// <summary>Target workspace. Present-in-digest, so cross-workspace application is not silent.</summary>
    public required PlanWorkspace Workspace { get; init; }

    /// <summary>Operations in the order they must be applied. Empty is a validation error.</summary>
    public required IReadOnlyList<PlanOperationDefinition> Operations { get; init; }
}
