using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// One operation declared by a plan file. The concrete record is a sealed derived
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

/// <summary>
/// Append one comment to a published work item. The effective HTML is captured in the
/// proposal so review, authorization, execution, and readback all bind to identical bytes.
/// </summary>
public sealed record AppendCommentOperation : PlanOperationDefinition
{
    /// <inheritdoc/>
    public override PlanOperationKind Kind => PlanOperationKind.AppendComment;

    /// <summary>The published work item receiving the comment.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>The explicitly selected saved Bench identity.</summary>
    public required string SavedBenchId { get; init; }

    /// <summary>The saved Bench contents digest reviewed by the authorizer.</summary>
    public required string ExpectedBenchContentsDigest { get; init; }

    /// <summary>The bound identity required to apply the proposal.</summary>
    public required string ExpectedIdentityId { get; init; }

    /// <summary>The submitted format: plain or markdown.</summary>
    public required string Format { get; init; }

    /// <summary>The exact submitted text.</summary>
    public required string Text { get; init; }

    /// <summary>The exact inert HTML body sent to the provider.</summary>
    public required string EffectiveBody { get; init; }
}

/// <summary>A provider-qualified work item reference bound to its durable Twig identity.</summary>
public sealed record ProviderWorkItemReference
{
    /// <summary>The durable provider-neutral Twig identity.</summary>
    public required LocalWorkItemId LocalId { get; init; }

    /// <summary>The exact provider, resource scope and opaque native identity.</summary>
    public required ExternalWorkItemIdentity ExternalIdentity { get; init; }
}

/// <summary>A provider work item and the provider-owned token required for mutation.</summary>
public sealed record ProviderWorkItemMutationTarget
{
    /// <summary>The canonical local and provider-qualified identities.</summary>
    public required ProviderWorkItemReference Item { get; init; }

    /// <summary>The exact opaque token observed from this provider.</summary>
    public required ProviderRevisionToken ExpectedProviderRevision { get; init; }
}

/// <summary>
/// Version 2 mutation intent. It is parseable and reviewable, but the current executor
/// refuses it before authorization or journal access.
/// </summary>
public sealed record ProviderMutationOperation : PlanOperationDefinition
{
    /// <summary>Exact revision protection by default; links may explicitly opt into observed non-atomic execution.</summary>
    public string Concurrency { get; init; } = "exact-revision";

    /// <summary>The supported field, link or delete operation kind.</summary>
    public required PlanOperationKind OperationKind { get; init; }

    /// <inheritdoc/>
    public override PlanOperationKind Kind => OperationKind;

    /// <summary>The mutation target and provider-owned expected revision.</summary>
    public required ProviderWorkItemMutationTarget Target { get; init; }

    /// <summary>Link peer; present only for link operations.</summary>
    public ProviderWorkItemReference? Other { get; init; }

    /// <summary>Link relation; present only for link operations.</summary>
    public string? Relation { get; init; }

    /// <summary>Field changes; present only for batch operations.</summary>
    public IReadOnlyDictionary<string, string?>? Fields { get; init; }
}
