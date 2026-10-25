namespace Twig.Domain.Services.Plan;

/// <summary>
/// The six kinds of operation a plan file may declare. Version 1 uses all kinds;
/// version 2 supports provider-bound batch, link and delete operations. Anything else is a schema
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

    /// <summary>Append one bounded discussion comment under an explicitly selected saved Bench.</summary>
    AppendComment,
}
