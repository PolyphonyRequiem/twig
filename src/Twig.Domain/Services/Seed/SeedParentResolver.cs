using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Single source of truth for reconciling the two places a seed's parent is recorded:
/// the denormalized <see cref="WorkItem.ParentId"/> field and <c>parent-child</c> rows in
/// the seed link table.
/// </summary>
/// <remarks>
/// Both stores are written locally and can disagree. Publish (<see cref="SeedPublishOrchestrator"/>)
/// and validation (<see cref="SeedValidator"/>) share this rule so that a wrong or ambiguous
/// parent is caught by <c>twig seed validate</c> rather than only at publish time.
/// </remarks>
public static class SeedParentResolver
{
    /// <summary>Rule name reported on parent-agreement validation failures.</summary>
    public const string RuleName = "ParentLink";

    /// <summary>
    /// Reconciles the seed's <see cref="WorkItem.ParentId"/> against its parent-child links.
    /// Returns the seed (possibly with an adopted ParentId), or a failure when the two stores
    /// disagree or the links are ambiguous.
    /// </summary>
    public static Result<WorkItem> Resolve(WorkItem seed, IReadOnlyList<SeedLink> links)
    {
        var parentTargets = GetParentTargets(seed, links);

        if (parentTargets.Length == 0)
            return Result.Ok(seed);

        if (parentTargets.Length > 1)
            return Result.Fail<WorkItem>(MultipleParentsMessage(seed));

        var linkedParentId = parentTargets[0];
        if (seed.ParentId.HasValue && seed.ParentId.Value != linkedParentId)
            return Result.Fail<WorkItem>(ConflictingParentsMessage(seed, linkedParentId));

        return seed.ParentId == linkedParentId
            ? Result.Ok(seed)
            : Result.Ok(seed.WithParentId(linkedParentId));
    }

    /// <summary>
    /// Validation-shaped view of <see cref="Resolve"/>: returns the failure a wrong or
    /// ambiguous parent would produce at publish time, or <c>null</c> when the stores agree.
    /// </summary>
    public static SeedValidationFailure? CheckParentAgreement(
        WorkItem seed,
        IReadOnlyList<SeedLink> links)
    {
        var parentTargets = GetParentTargets(seed, links);

        if (parentTargets.Length > 1)
            return new SeedValidationFailure(RuleName, MultipleParentsMessage(seed));

        if (parentTargets.Length == 1 &&
            seed.ParentId.HasValue &&
            seed.ParentId.Value != parentTargets[0])
        {
            return new SeedValidationFailure(RuleName, ConflictingParentsMessage(seed, parentTargets[0]));
        }

        return null;
    }

    private static int[] GetParentTargets(WorkItem seed, IReadOnlyList<SeedLink> links) =>
        links
            .Where(link =>
                link.LinkType == SeedLinkTypes.ParentChild &&
                link.SourceId == seed.Id)
            .Select(link => link.TargetId)
            .Distinct()
            .ToArray();

    private static string MultipleParentsMessage(WorkItem seed) =>
        $"Seed {seed.Id} ('{seed.Title}') has multiple parent-child links. Remove the extra parent links before publishing.";

    private static string ConflictingParentsMessage(WorkItem seed, int linkedParentId) =>
        $"Seed {seed.Id} ('{seed.Title}') has conflicting parents: ParentId is {seed.ParentId!.Value}, but its parent-child link targets {linkedParentId}.";
}
