using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Projects a validated Change Proposal into its canonical semantic review model.
/// <para>
/// The model is a <em>derived projection</em>: it embeds the proposal's digest and never
/// contributes to it. That is why affected-item context may be enriched with live board data
/// without destabilising an authorization — see
/// <see cref="ChangeProposalReviewModel"/> for the full rule.
/// </para>
/// <para>
/// <b>Enrichment reads the local cache only.</b> Preview is a non-mutating, offline path
/// today; issuing a network refresh per affected item would add latency and a new failure
/// mode to a path whose entire job is to describe a document. An item the cache does not know
/// is emitted with a null type/title/state rather than omitted — dropping it would hide an
/// affected item from the reviewer, which is the one outcome the model exists to prevent.
/// </para>
/// </summary>
public sealed class ChangeProposalReviewModelBuilder(IWorkItemRepository workItems)
{
    private readonly IWorkItemRepository _workItems = workItems
        ?? throw new ArgumentNullException(nameof(workItems));

    /// <summary>Authorization choices offered when the proposal is currently applicable.</summary>
    private static readonly string[] ApplicableChoices = ["apply", "revise", "decline"];

    /// <summary>
    /// Choices offered when something blocks apply. <c>apply</c> is withheld deliberately:
    /// presenting a control that is guaranteed to refuse misrepresents the decision the
    /// reviewer is being asked to make.
    /// </summary>
    private static readonly string[] BlockedChoices = ["revise", "decline"];

    /// <summary>
    /// Builds the model for <paramref name="definition"/>.
    /// </summary>
    public async Task<ChangeProposalReviewModel> BuildAsync(
        PlanDefinition definition,
        string digest,
        IReadOnlyList<PlanValidationIssue> issues,
        IReadOnlyList<PendingChangeDetail> pendingChanges,
        bool canApply,
        string? rationale = null,
        ChangeRecipeReference? recipe = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);

        var operations = new List<ReviewOperation>(definition.Operations.Count);
        var roles = new Dictionary<int, string>();

        for (var ordinal = 0; ordinal < definition.Operations.Count; ordinal++)
        {
            var op = definition.Operations[ordinal];
            operations.Add(ProjectOperation(ordinal, op, roles));
        }

        var affected = await EnrichAsync(roles, ct).ConfigureAwait(false);

        return new ChangeProposalReviewModel
        {
            Digest = digest,
            Workspace = definition.Workspace,
            Rationale = rationale,
            Recipe = recipe,
            AffectedItems = affected,
            Operations = operations,
            AuthorizationChoices = canApply ? ApplicableChoices : BlockedChoices,
            Blockers = ProjectBlockers(issues, pendingChanges),
        };
    }

    private static ReviewOperation ProjectOperation(
        int ordinal,
        PlanOperationDefinition op,
        Dictionary<int, string> roles)
    {
        switch (op)
        {
            case BatchOperation batch:
            {
                MarkTarget(roles, batch.WorkItemId);
                var consequences = new List<ReviewConsequence>(batch.Fields.Count);
                foreach (var (field, value) in batch.Fields)
                {
                    consequences.Add(new ReviewConsequence
                    {
                        // A null value clears the field. That is a materially different act
                        // from setting it, so it gets its own kind rather than a set with a
                        // null payload a renderer might print as the word "null".
                        Kind = value is null ? "field-clear" : "field-set",
                        Field = field,
                        To = value,
                    });
                }

                return new ReviewOperation
                {
                    Ordinal = ordinal,
                    OpId = batch.Id,
                    Kind = PlanDocumentWriter.WireKind(batch.Kind),
                    Target = new ReviewTarget { WorkItemId = batch.WorkItemId },
                    Summary = $"Set {Plural(batch.Fields.Count, "field")} on #{batch.WorkItemId}",
                    Preconditions = [Revision(batch.ExpectedRevision)],
                    Consequences = consequences,
                };
            }

            case AddLinkOperation add:
                MarkTarget(roles, add.WorkItemId);
                MarkPeer(roles, add.OtherId);
                return new ReviewOperation
                {
                    Ordinal = ordinal,
                    OpId = add.Id,
                    Kind = PlanDocumentWriter.WireKind(add.Kind),
                    Target = new ReviewTarget { WorkItemId = add.WorkItemId },
                    Summary = $"Add {add.Relation} link #{add.WorkItemId} -> #{add.OtherId}",
                    Preconditions = [Revision(add.ExpectedRevision)],
                    Consequences =
                    [
                        new ReviewConsequence
                        {
                            Kind = "link-add",
                            Relation = add.Relation,
                            OtherId = add.OtherId,
                        },
                    ],
                };

            case RemoveLinkOperation remove:
                MarkTarget(roles, remove.WorkItemId);
                MarkPeer(roles, remove.OtherId);
                return new ReviewOperation
                {
                    Ordinal = ordinal,
                    OpId = remove.Id,
                    Kind = PlanDocumentWriter.WireKind(remove.Kind),
                    Target = new ReviewTarget { WorkItemId = remove.WorkItemId },
                    Summary = $"Remove {remove.Relation} link #{remove.WorkItemId} -> #{remove.OtherId}",
                    Preconditions = [Revision(remove.ExpectedRevision)],
                    Consequences =
                    [
                        new ReviewConsequence
                        {
                            Kind = "link-remove",
                            Relation = remove.Relation,
                            OtherId = remove.OtherId,
                        },
                    ],
                };

            case PublishSeedOperation seed:
            {
                // A staged seed has no work item id until it is published, so it contributes
                // no affected item. Synthesising the negative alias here would put a number
                // in front of a reviewer that means nothing on the board.
                var identity = seed.StagedIdentity.Value.ToString();
                return new ReviewOperation
                {
                    Ordinal = ordinal,
                    OpId = seed.Id,
                    Kind = PlanDocumentWriter.WireKind(seed.Kind),
                    Target = new ReviewTarget { StagedIdentity = identity },
                    Summary = $"Publish staged seed {identity}",
                    Preconditions =
                    [
                        new ReviewPrecondition
                        {
                            Kind = "expectedFingerprint",
                            Value = seed.ExpectedFingerprint,
                        },
                    ],
                    Consequences =
                    [
                        new ReviewConsequence { Kind = "seed-publish" },
                    ],
                };
            }

            case DeleteOperation delete:
                MarkTarget(roles, delete.WorkItemId);
                return new ReviewOperation
                {
                    Ordinal = ordinal,
                    OpId = delete.Id,
                    Kind = PlanDocumentWriter.WireKind(delete.Kind),
                    Target = new ReviewTarget { WorkItemId = delete.WorkItemId },
                    Summary = $"Delete #{delete.WorkItemId}",
                    Preconditions = [Revision(delete.ExpectedRevision)],
                    Consequences =
                    [
                        new ReviewConsequence { Kind = "work-item-delete", OtherId = delete.WorkItemId },
                    ],
                };

            default:
                throw new NotSupportedException(
                    $"Plan operation kind '{op.Kind}' has no review projection. Every kind must be " +
                    "renderable, because a reviewer may never be shown a proposal with an operation missing.");
        }
    }

    private static ReviewPrecondition Revision(int expectedRevision) => new()
    {
        Kind = "expectedRevision",
        Value = expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // "target" always wins over "peer": an item an operation acts on directly is not demoted
    // because a later operation happens to link to it.
    private static void MarkTarget(Dictionary<int, string> roles, int id) => roles[id] = "target";

    private static void MarkPeer(Dictionary<int, string> roles, int id)
    {
        if (!roles.ContainsKey(id))
            roles[id] = "peer";
    }

    private async Task<IReadOnlyList<ReviewAffectedItem>> EnrichAsync(
        Dictionary<int, string> roles,
        CancellationToken ct)
    {
        if (roles.Count == 0)
            return [];

        var ids = roles.Keys.OrderBy(static id => id).ToArray();
        var known = await _workItems.GetByIdsAsync(ids, ct).ConfigureAwait(false);
        var byId = known.ToDictionary(static item => item.Id);

        var affected = new List<ReviewAffectedItem>(ids.Length);
        foreach (var id in ids)
        {
            byId.TryGetValue(id, out var item);
            affected.Add(new ReviewAffectedItem
            {
                Id = id,
                Type = item?.Type.Value,
                Title = item?.Title,
                State = item?.State,
                Role = roles[id],
            });
        }

        return affected;
    }

    private static IReadOnlyList<ReviewBlocker> ProjectBlockers(
        IReadOnlyList<PlanValidationIssue> issues,
        IReadOnlyList<PendingChangeDetail> pendingChanges)
    {
        if (issues.Count == 0 && pendingChanges.Count == 0)
            return [];

        var blockers = new List<ReviewBlocker>(issues.Count + pendingChanges.Count);

        foreach (var issue in issues)
        {
            blockers.Add(new ReviewBlocker
            {
                Kind = "issue",
                Detail = string.IsNullOrEmpty(issue.Path)
                    ? $"{issue.Code}: {issue.Message}"
                    : $"{issue.Code} at {issue.Path}: {issue.Message}",
            });
        }

        foreach (var pending in pendingChanges)
        {
            blockers.Add(new ReviewBlocker
            {
                Kind = "pending",
                WorkItemId = pending.WorkItemId,
                Detail = pending.Field is { Length: > 0 } field
                    ? $"staged {pending.Kind} on {field}"
                    : $"staged {pending.Kind}",
            });
        }

        return blockers;
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
