using Twig.Domain.Aggregates;
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

    private IFieldDefinitionStore? _fields;

    /// <summary>Enrich with locally cached field labels and types.</summary>
    public ChangeProposalReviewModelBuilder(IWorkItemRepository workItems, IFieldDefinitionStore fields) : this(workItems)
        => _fields = fields;

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

        var known = roles.Count == 0 ? [] : await _workItems.GetByIdsAsync(roles.Keys, ct).ConfigureAwait(false);
        var byId = known.ToDictionary(i => i.Id);
        var affected = roles.OrderBy(p => p.Key).Select(p => Item(p.Key, p.Value, byId.GetValueOrDefault(p.Key), definition.Workspace)).ToArray();
        var fieldDefinitions = _fields is null ? [] : await _fields.GetAllAsync(ct).ConfigureAwait(false);
        var labels = fieldDefinitions.ToDictionary(f => f.ReferenceName, StringComparer.OrdinalIgnoreCase);
        var seeds = definition.Operations.Any(o => o is PublishSeedOperation)
            ? await _workItems.GetSeedsAsync(ct).ConfigureAwait(false) : [];
        var staged = seeds.Where(s => s.Id < 0 && s.StagedIdentity is not null)
            .ToDictionary(s => s.StagedIdentity!.Value.Value.ToString());
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            if (definition.Operations[i] is BatchOperation batch)
            {
                var item = byId.GetValueOrDefault(batch.WorkItemId);
                operations[i] = op with { Consequences = op.Consequences.Select(c =>
                {
                    var before = Before(item, batch.ExpectedRevision, c.Field!, pendingChanges.Any(p => p.WorkItemId == batch.WorkItemId));
                    labels.TryGetValue(c.Field!, out var metadata);
                    return c with { FieldLabel = metadata?.DisplayName ?? c.Field, FieldType = metadata?.DataType,
                        Before = before, TextChange = string.Equals(c.Field, "System.Description", StringComparison.OrdinalIgnoreCase) && before.State != "unknown"
                            ? ReviewTextChange.Measure(before.Value ?? "", c.To ?? "") : null };
                }).ToArray() };
            }
            else if (op.Target.StagedIdentity is { } identity && staged.TryGetValue(identity, out var seed))
                operations[i] = op with { Target = op.Target with { Seed = new ReviewSeedDisplay
                { DisplayAlias = seed.Id, Title = seed.Title, Type = seed.Type.Value, State = seed.State, ParentId = seed.ParentId } } };
        }
        // Resolve display ambiguity once for every presenter, across requested effects only.
        // Repeated uses of one reference are not a collision; unrelated cached fields do not expand labels.
        var ambiguousLabels = operations.SelectMany(o => o.Consequences).Where(c => c.Field is not null)
            .GroupBy(c => c.FieldLabel ?? c.Field!, StringComparer.Ordinal)
            .Where(g => g.Select(c => c.Field!).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (ambiguousLabels.Count > 0)
            for (var i = 0; i < operations.Count; i++)
                operations[i] = operations[i] with { Consequences = operations[i].Consequences.Select(c =>
                    c.Field is not null && ambiguousLabels.Contains(c.FieldLabel ?? c.Field)
                        ? c with { FieldLabel = $"{c.FieldLabel ?? c.Field} ({c.Field})" } : c).ToArray() };

        // One hop only. Missing parents remain named context; never recurse through a corrupt cycle.
        var parentIds = affected.Select(i => i.ParentId).Concat(operations.Select(o => o.Target.Seed?.ParentId))
            .Where(id => id is not null && !roles.ContainsKey(id.Value)).Select(id => id!.Value).Distinct().Order().ToArray();
        var parents = parentIds.Length == 0 ? [] : await _workItems.GetByIdsAsync(parentIds, ct).ConfigureAwait(false);
        var parentMap = parents.ToDictionary(i => i.Id);
        var context = parentIds.Select(id => Item(id, "context", parentMap.GetValueOrDefault(id), definition.Workspace)).ToArray();

        return new ChangeProposalReviewModel
        {
            Digest = digest,
            Workspace = definition.Workspace,
            Rationale = rationale,
            Recipe = recipe,
            AffectedItems = affected,
            ContextItems = context,
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

    private static ReviewAffectedItem Item(int id, string role, WorkItem? item, PlanWorkspace workspace) => new()
    {
        Id = id, Role = role, Title = item?.Title, Type = item?.Type.Value, State = item?.State,
        ParentId = item?.ParentId, Revision = item?.Revision,
        Url = id > 0 ? $"https://dev.azure.com/{Uri.EscapeDataString(workspace.Organization)}/{Uri.EscapeDataString(workspace.Project)}/_workitems/edit/{id}" : null,
    };

    private static ReviewBeforeValue Before(WorkItem? item, int expectedRevision, string field, bool pending)
    {
        string? reason = item is null ? "item-not-cached"
            : item.Revision != expectedRevision || item.Revision <= 0 ? "revision-mismatch"
            : item.IsDirty || pending ? "local-edits" : null;
        string? value = null;
        if (reason is null)
        {
            // Canonical properties are hydrated separately from the arbitrary field bag.
            switch (field.ToLowerInvariant())
            {
                case "system.title": value = item!.Title; break;
                case "system.state": value = item!.State; break;
                case "system.assignedto": value = item!.AssignedTo; break;
                case "system.areapath": value = item!.AreaPath.Value; break;
                case "system.iterationpath": value = item!.IterationPath.Value; break;
                default:
                    if (!item!.Fields.TryGetValue(field, out value)) reason = "field-not-cached";
                    break;
            }
        }
        return new ReviewBeforeValue { State = reason is not null ? "unknown" : value is null ? "absent" : "value",
            Value = reason is null ? value : null, Revision = item?.Revision, Reason = reason };
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
