using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// A stable, source-of-truth description of a single staged seed as it currently lives in
/// the local cache. The plan lifecycle emits one so a caller composing a plan file can copy
/// the <see cref="Identity"/> and <see cref="Fingerprint"/> verbatim into a
/// <c>publish-seed</c> operation without inventing either — the identity is the durable key
/// and the fingerprint is the exact drift-detector the apply pass will recompute.
/// </summary>
/// <remarks>
/// Read-only: describing a seed never mutates it. A seed that has already been published,
/// or an id that is not a seed, has no descriptor and the lifecycle returns <c>null</c>.
/// </remarks>
public sealed record PlanSeedDescriptor
{
    /// <summary>The durable identity minted when the seed was staged (wayfinder 0014).</summary>
    public required StagedIdentity Identity { get; init; }

    /// <summary>The negative display alias the user sees; decorative, never a key.</summary>
    public required StagedAlias Alias { get; init; }

    /// <summary>
    /// Canonical fingerprint of the seed's current fields and all its local seed links.
    /// The plan apply pass recomputes this over the same inputs and refuses the operation
    /// on drift, so pasting it into a plan pins the seed to exactly the shape it had here.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>The seed's current title.</summary>
    public required string Title { get; init; }

    /// <summary>The seed's work item type name (e.g. <c>Task</c>, <c>User Story</c>).</summary>
    public required string Type { get; init; }
}
