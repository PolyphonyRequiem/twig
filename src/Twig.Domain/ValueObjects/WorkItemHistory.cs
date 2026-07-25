namespace Twig.Domain.ValueObjects;

/// <summary>
/// Complete, chronologically ordered revision history for a single work item (twig#241).
/// </summary>
/// <param name="WorkItemId">The work item the timeline belongs to.</param>
/// <param name="Complete">
/// <c>true</c> when the traversal saw every ADO page. A partial timeline is never reported
/// as success — any page failure surfaces as an error instead. Relation-target enrichment
/// failures must never affect this flag.
/// </param>
/// <param name="Events">Events ordered ascending by <see cref="WorkItemHistoryEvent.UpdateId"/>.</param>
public sealed record WorkItemHistory(
    int WorkItemId,
    bool Complete,
    IReadOnlyList<WorkItemHistoryEvent> Events);

/// <summary>
/// A single work-item update record.
/// </summary>
/// <param name="UpdateId">
/// ADO's <c>updateId</c> — the ordering key and unique event identity. NOT the revision:
/// relation changes emit their own update records without bumping the revision, so several
/// update IDs commonly share one revision.
/// </param>
/// <param name="Revision">The work item revision this update produced. Not unique per event.</param>
/// <param name="ChangedAt">
/// Normalized change time: <c>System.ChangedDate.newValue</c> when present, else the record's
/// <c>revisedDate</c>. ADO's <c>9999-01-01T00:00:00Z</c> sentinel is never emitted here.
/// </param>
/// <param name="ChangedBy">Display name of the actor who made the change.</param>
/// <param name="ChangedByIdentity">Full identity (unique name / email) — detail only.</param>
/// <param name="ChangedFields">
/// Reference names of the fields this update changed, with bookkeeping fields suppressed.
/// Always populated (brief and detailed).
/// </param>
/// <param name="Fields">Field deltas with old and new values. Populated only when <paramref name="Detailed"/>.</param>
/// <param name="Relations">Work-item↔work-item relation additions and removals. Always populated.</param>
/// <param name="Detailed">Whether this event carries full field deltas.</param>
public sealed record WorkItemHistoryEvent(
    int UpdateId,
    int Revision,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    string? ChangedByIdentity,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<WorkItemFieldChange> Fields,
    IReadOnlyList<WorkItemRelationChange> Relations,
    bool Detailed);

/// <summary>
/// A single field delta. Entries where both sides are null are suppressed upstream — ADO emits
/// those on item creation for fields that were never set. Genuine one-sided nulls are preserved:
/// an initial value is <c>(null, X)</c> and a cleared field is <c>(X, null)</c>.
/// </summary>
public sealed record WorkItemFieldChange(
    string ReferenceName,
    string? OldValue,
    string? NewValue);

/// <summary>Whether a relation was added or removed by an update.</summary>
public enum RelationChangeKind
{
    Added,
    Removed,
}

/// <summary>
/// A work-item↔work-item relation change. Non-work-item relations (ArtifactLink, Hyperlink)
/// are skipped during extraction and never appear here.
/// </summary>
/// <param name="Kind">Added or removed.</param>
/// <param name="RelationType">The raw ADO relation type, preserved verbatim.</param>
/// <param name="TargetId">The target work item ID parsed from the relation URL.</param>
/// <param name="Target">Enrichment for the target, when resolvable.</param>
public sealed record WorkItemRelationChange(
    RelationChangeKind Kind,
    string RelationType,
    int TargetId,
    WorkItemRelationTarget? Target);

/// <summary>
/// Enrichment for a relation target. An unresolvable target is reported with
/// <see cref="Deleted"/> set rather than as an absent title — "the parent was deleted"
/// is itself a finding.
/// </summary>
public sealed record WorkItemRelationTarget(
    int Id,
    string? Title,
    string? Type,
    string? State,
    bool Deleted);

/// <summary>
/// Caller-supplied options controlling history projection. Read-only: no workspace, cache,
/// context, or pending-change mutation results from any history request.
/// </summary>
/// <param name="DetailAll">When true, every event carries full field deltas.</param>
/// <param name="DetailUpdateIds">Specific update IDs to render in full detail.</param>
/// <param name="Fields">
/// ADO reference names to restrict field deltas to. When non-empty, unrelated field deltas are
/// removed but relation events are preserved — filtering for State must not silently hide a
/// reparent. An update is retained if it has a matching field delta OR a relation event.
/// </param>
public sealed record WorkItemHistoryOptions(
    bool DetailAll = false,
    IReadOnlySet<int>? DetailUpdateIds = null,
    IReadOnlySet<string>? Fields = null)
{
    public static readonly WorkItemHistoryOptions Brief = new();

    public bool IsDetailed(int updateId) =>
        DetailAll || (DetailUpdateIds is not null && DetailUpdateIds.Contains(updateId));

    public bool HasFieldFilter => Fields is { Count: > 0 };
}
