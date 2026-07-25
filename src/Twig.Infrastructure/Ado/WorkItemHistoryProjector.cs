using System.Globalization;
using System.Text.Json;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Projects raw ADO update records into the emitted <see cref="WorkItemHistory"/> contract
/// (twig#241). A pure function over parsed DTOs — no I/O, no HTTP — so every contract rule
/// (bookkeeping suppression, <c>changedAt</c> normalization, both-null suppression,
/// null-vs-absent handling, relation extraction) is directly testable.
/// </summary>
internal static class WorkItemHistoryProjector
{
    /// <summary>
    /// ADO's sentinel <c>revisedDate</c> on the current revision. Must never be emitted as a
    /// normalized change time.
    /// </summary>
    internal const string RevisedDateSentinel = "9999-01-01T00:00:00Z";

    internal const string ChangedDateField = "System.ChangedDate";

    /// <summary>
    /// Fields that appear on essentially every field update and carry no user-facing signal.
    /// Omitted from the brief <c>changed</c> list; still available in detail and when named
    /// explicitly via a field filter.
    /// </summary>
    internal static readonly IReadOnlySet<string> BookkeepingFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Rev",
            "System.AuthorizedDate",
            "System.RevisedDate",
            "System.ChangedDate",
            "System.Watermark",
        };

    /// <summary>URL segment that marks a relation as pointing at a work item.</summary>
    private const string WorkItemUrlSegment = "/_apis/wit/workitems/";

    public static WorkItemHistory Project(
        int workItemId,
        IReadOnlyList<AdoWorkItemUpdate> updates,
        WorkItemHistoryOptions options,
        IReadOnlyDictionary<int, WorkItemRelationTarget>? enrichment = null)
    {
        var events = new List<WorkItemHistoryEvent>(updates.Count);

        foreach (var update in updates.OrderBy(u => u.Id))
        {
            var detailed = options.IsDetailed(update.Id);
            var fields = ProjectFields(update, options);
            var relations = ProjectRelations(update, enrichment);

            // A field filter removes unrelated field deltas but never removes relation events.
            // An update is retained if it has a matching field delta or a relation event.
            if (options.HasFieldFilter && fields.Count == 0 && relations.Count == 0)
                continue;

            var changedFields = fields
                .Where(f => options.HasFieldFilter || !BookkeepingFields.Contains(f.ReferenceName))
                .Select(f => f.ReferenceName)
                .ToList();

            events.Add(new WorkItemHistoryEvent(
                UpdateId: update.Id,
                Revision: update.Rev,
                ChangedAt: NormalizeChangedAt(update),
                ChangedBy: update.RevisedBy?.DisplayName,
                ChangedByIdentity: detailed ? update.RevisedBy?.UniqueName : null,
                ChangedFields: changedFields,
                Fields: detailed ? fields : Array.Empty<WorkItemFieldChange>(),
                Relations: relations,
                Detailed: detailed));
        }

        return new WorkItemHistory(workItemId, Complete: true, Events: events);
    }

    // ── changedAt normalization ─────────────────────────────────────

    /// <summary>
    /// <c>System.ChangedDate.newValue</c> when present, else the record's own
    /// <c>revisedDate</c>. The fallback is not an edge case: relation-only update records
    /// carry no <c>fields</c> at all. The <c>9999-01-01</c> sentinel is never emitted.
    /// </summary>
    internal static DateTimeOffset? NormalizeChangedAt(AdoWorkItemUpdate update)
    {
        if (update.Fields is not null &&
            update.Fields.TryGetValue(ChangedDateField, out var changedDate) &&
            TryReadString(changedDate.NewValue, out var changed) &&
            TryParseNonSentinel(changed, out var fromField))
        {
            return fromField;
        }

        return TryParseNonSentinel(update.RevisedDate, out var fromRecord) ? fromRecord : null;
    }

    private static bool TryParseNonSentinel(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (string.Equals(value, RevisedDateSentinel, StringComparison.OrdinalIgnoreCase)) return false;

        if (!DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
        {
            return false;
        }

        // Defensive: a differently-formatted sentinel must not slip through.
        return parsed.Year != 9999;
    }

    // ── Field projection ────────────────────────────────────────────

    private static List<WorkItemFieldChange> ProjectFields(
        AdoWorkItemUpdate update,
        WorkItemHistoryOptions options)
    {
        var result = new List<WorkItemFieldChange>();
        if (update.Fields is null) return result;

        foreach (var (referenceName, delta) in update.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (options.HasFieldFilter && !options.Fields!.Contains(referenceName))
                continue;

            var hasOld = HasValue(delta.OldValue);
            var hasNew = HasValue(delta.NewValue);

            // ADO emits field entries on creation for fields that were never set, producing
            // deltas where BOTH sides are null. Those do not constitute a change.
            if (!hasOld && !hasNew) continue;

            result.Add(new WorkItemFieldChange(
                referenceName,
                hasOld ? RenderValue(delta.OldValue!.Value) : null,
                hasNew ? RenderValue(delta.NewValue!.Value) : null));
        }

        return result;
    }

    private static bool HasValue(JsonElement? element) =>
        element.HasValue && element.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>
    /// Renders a field value as a string. Identity-valued fields (AssignedTo, ChangedBy) arrive
    /// as objects; their <c>displayName</c> is the useful projection.
    /// </summary>
    internal static string RenderValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object when element.TryGetProperty("displayName", out var name)
            && name.ValueKind == JsonValueKind.String => name.GetString() ?? string.Empty,
        _ => element.GetRawText(),
    };

    private static bool TryReadString(JsonElement? element, out string? value)
    {
        value = null;
        if (!HasValue(element)) return false;
        if (element!.Value.ValueKind != JsonValueKind.String) return false;
        value = element.Value.GetString();
        return value is not null;
    }

    // ── Relation projection ─────────────────────────────────────────

    private static List<WorkItemRelationChange> ProjectRelations(
        AdoWorkItemUpdate update,
        IReadOnlyDictionary<int, WorkItemRelationTarget>? enrichment)
    {
        var result = new List<WorkItemRelationChange>();
        if (update.Relations is null) return result;

        Collect(update.Relations.Added, RelationChangeKind.Added);
        Collect(update.Relations.Removed, RelationChangeKind.Removed);
        return result;

        void Collect(List<AdoRelation>? relations, RelationChangeKind kind)
        {
            if (relations is null) return;

            foreach (var relation in relations)
            {
                if (!TryExtractWorkItemId(relation.Url, out var targetId)) continue;

                WorkItemRelationTarget? target = null;
                if (enrichment is not null)
                {
                    target = enrichment.TryGetValue(targetId, out var resolved)
                        ? resolved
                        // Unresolvable target ⇒ report as deleted, not as a null title.
                        : new WorkItemRelationTarget(targetId, null, null, null, Deleted: true);
                }

                result.Add(new WorkItemRelationChange(
                    kind,
                    relation.Rel ?? string.Empty,
                    targetId,
                    target));
            }
        }
    }

    /// <summary>
    /// Extracts the target work-item ID from a relation URL. Only work-item↔work-item relations
    /// qualify. Guards on BOTH the <c>/_apis/wit/workItems/</c> segment AND a numeric tail —
    /// real histories carry <c>ArtifactLink</c> relations whose URL is
    /// <c>vstfs:///Git/Commit/&lt;guid&gt;%2f&lt;guid&gt;%2f&lt;sha&gt;</c>, which a naive parser throws on.
    /// </summary>
    internal static bool TryExtractWorkItemId(string? url, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var segmentIndex = url.IndexOf(WorkItemUrlSegment, StringComparison.OrdinalIgnoreCase);
        if (segmentIndex < 0) return false;

        var tail = url[(segmentIndex + WorkItemUrlSegment.Length)..];

        // Trim any query string or trailing path so only the ID candidate remains.
        var cut = tail.AsSpan().IndexOfAny('?', '/', '#');
        if (cut >= 0) tail = tail[..cut];

        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>
    /// Every distinct work-item relation target across the given updates — the input to the
    /// single batch enrichment call.
    /// </summary>
    internal static IReadOnlyList<int> CollectRelationTargetIds(IReadOnlyList<AdoWorkItemUpdate> updates)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();

        foreach (var update in updates)
        {
            if (update.Relations is null) continue;

            foreach (var relation in Enumerable.Concat(
                         update.Relations.Added ?? [],
                         update.Relations.Removed ?? []))
            {
                if (TryExtractWorkItemId(relation.Url, out var id) && seen.Add(id))
                    ids.Add(id);
            }
        }

        return ids;
    }
}
