namespace Twig.Domain.Services.Plan;

/// <summary>
/// The five kinds of operation a Plan v1 file may declare. Anything else is a schema
/// violation surfaced as a <see cref="PlanValidationIssue"/>, never coerced.
/// </summary>
public enum PlanOperationKind
{
    /// <summary>Field/state edits applied in one exact-revision ADO PATCH.</summary>
    Batch,

    /// <summary>Add a link of the given relation from source work item to another id.</summary>
    AddLink,

    /// <summary>Remove a link of the given relation from source work item to another id.</summary>
    RemoveLink,

    /// <summary>Publish exactly one staged seed by <see cref="Twig.Domain.ValueObjects.StagedIdentity"/>.</summary>
    PublishSeed,

    /// <summary>Delete a work item at an exact expected revision.</summary>
    Delete,
}
