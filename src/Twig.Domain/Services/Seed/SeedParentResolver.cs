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

    /// <summary>Rule name reported on the advisory inferred-parent warning (twig#260).</summary>
    public const string InferredParentRuleName = "InferredParent";

    /// <summary>
    /// Advisory check for a parent that was never explicitly chosen (twig#260).
    /// </summary>
    /// <remarks>
    /// Every path that sets a parent deliberately — <c>seed new --parent</c>,
    /// <c>seed link --type parent-child</c>, and the MCP equivalents — writes BOTH the
    /// denormalized <see cref="WorkItem.ParentId"/> and a parent-child link row. The
    /// inference fallback in <c>seed new</c> writes only <c>ParentId</c>. So a seed with
    /// <c>ParentId</c> set and zero parent-child rows is one whose parent was inherited
    /// from the active item rather than chosen — the twig#254 case.
    /// <para>
    /// This is advisory, never a failure: an inferred parent is frequently the right one,
    /// and <c>seed validate</c> must still exit 0. Seeds created before this convention
    /// landed will also warn; that is accepted rather than version-gated.
    /// </para>
    /// </remarks>
    public static SeedValidationFailure? CheckInferredParent(
        WorkItem seed,
        IReadOnlyList<SeedLink> links)
    {
        if (!seed.ParentId.HasValue)
            return null;

        if (GetParentTargets(seed, links).Length > 0)
            return null;

        return new SeedValidationFailure(
            InferredParentRuleName,
            $"Seed {seed.Id} ('{seed.Title}') has parent #{seed.ParentId.Value}, but it was never " +
            "explicitly chosen — it was inherited from the active work item at creation time. " +
            $"Confirm it with 'twig seed link {seed.Id} {seed.ParentId.Value} --type parent-child', " +
            "or change it with 'twig seed link' to a different parent.");
    }

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
