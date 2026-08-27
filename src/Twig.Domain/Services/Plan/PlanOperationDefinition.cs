using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// One operation declared by a plan v1 file. The concrete record is a sealed derived
/// type — parsing validates every kind-specific required/forbidden property against the
/// exact contract before promoting a JSON object into one of these.
/// </summary>
public abstract record PlanOperationDefinition
{
    /// <summary>The plan-unique operation id. Duplicates are a validation error.</summary>
    public required string Id { get; init; }

    /// <summary>The kind discriminator. Fixed per derived record.</summary>
    public abstract PlanOperationKind Kind { get; }
}

/// <summary>
/// Field/state edits applied in a single exact-revision ADO PATCH. Never carries a note
/// or artifact — those are separate seed concerns and are explicitly out of scope for the
/// plan surface.
/// </summary>
public sealed record BatchOperation : PlanOperationDefinition
{
    /// <inheritdoc/>
    public override PlanOperationKind Kind => PlanOperationKind.Batch;

    /// <summary>The target work item id.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>The revision the PATCH must be issued against.</summary>
    public required int ExpectedRevision { get; init; }

    /// <summary>
    /// The fields to write. Values may be null to clear a field. The map must be
    /// non-empty — a batch with no fields is a validation error.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> Fields { get; init; }
}

/// <summary>
/// Add one relation from the source work item to another id.
/// </summary>
public sealed record AddLinkOperation : PlanOperationDefinition
{
    /// <inheritdoc/>
    public override PlanOperationKind Kind => PlanOperationKind.AddLink;

    /// <summary>The source work item; expected revision applies here.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>Source revision the link mutation must be issued against.</summary>
    public required int ExpectedRevision { get; init; }

    /// <summary>The relation kind: parent | predecessor | successor | related.</summary>
    public required string Relation { get; init; }

    /// <summary>The other work item id.</summary>
    public required int OtherId { get; init; }
}

/// <summary>
/// Remove one relation from the source work item to another id.
/// </summary>
public sealed record RemoveLinkOperation : PlanOperationDefinition
{
    /// <inheritdoc/>
    public override PlanOperationKind Kind => PlanOperationKind.RemoveLink;

    /// <summary>The source work item; expected revision applies here.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>Source revision the link mutation must be issued against.</summary>
    public required int ExpectedRevision { get; init; }

    /// <summary>The relation kind: parent | predecessor | successor | related.</summary>
    public required string Relation { get; init; }

    /// <summary>The other work item id.</summary>
    public required int OtherId { get; init; }
}

/// <summary>
/// Publish exactly one staged seed. A plan may declare more than one publish-seed op,
/// but every one targets a distinct staged identity — that is what the parser enforces.
/// </summary>
public sealed record PublishSeedOperation : PlanOperationDefinition
{
    /// <inheritdoc/>
    public override PlanOperationKind Kind => PlanOperationKind.PublishSeed;

    /// <summary>The seed's durable identity (GUIDv7).</summary>
    public required StagedIdentity StagedIdentity { get; init; }

    /// <summary>Fingerprint the seed must still hash to when the operation is applied.</summary>
    public required string ExpectedFingerprint { get; init; }
}

/// <summary>
/// Delete a work item at an exact expected revision.
/// </summary>
public sealed record DeleteOperation : PlanOperationDefinition
{
    /// <inheritdoc/>
    public override PlanOperationKind Kind => PlanOperationKind.Delete;

    /// <summary>The work item id to delete.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>The revision the delete must be issued against.</summary>
    public required int ExpectedRevision { get; init; }
}
