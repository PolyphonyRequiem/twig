using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO Work Item Updates API response page (<c>GET .../workItems/{id}/updates</c>).
/// </summary>
/// <remarks>
/// <c>count</c> reflects the current page only, not total history — verified as
/// <c>[10, 10, 8, 0]</c> across a 28-update item at <c>$top=10</c>. Pagination termination
/// must key on a short page, never on this value.
/// </remarks>
internal sealed class AdoWorkItemUpdatesResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoWorkItemUpdate>? Value { get; set; }
}

/// <summary>
/// A single update record. Relation-only updates carry no <c>fields</c> at all — those are
/// precisely the reparenting events history exists to surface.
/// </summary>
internal sealed class AdoWorkItemUpdate
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("workItemId")]
    public int WorkItemId { get; set; }

    [JsonPropertyName("rev")]
    public int Rev { get; set; }

    [JsonPropertyName("revisedBy")]
    public AdoUpdateIdentity? RevisedBy { get; set; }

    /// <summary>
    /// Carries ADO's <c>9999-01-01T00:00:00Z</c> sentinel on the current revision.
    /// Kept as a raw string so the sentinel is detectable rather than silently parsed
    /// into a real-looking timestamp.
    /// </summary>
    [JsonPropertyName("revisedDate")]
    public string? RevisedDate { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, AdoFieldUpdate>? Fields { get; set; }

    [JsonPropertyName("relations")]
    public AdoRelationUpdates? Relations { get; set; }
}

/// <summary>
/// A field delta. Both properties absent means ADO emitted the entry on creation for a field
/// that was never set — such entries do not constitute a change and are suppressed.
/// </summary>
internal sealed class AdoFieldUpdate
{
    [JsonPropertyName("oldValue")]
    public JsonElement? OldValue { get; set; }

    [JsonPropertyName("newValue")]
    public JsonElement? NewValue { get; set; }
}

internal sealed class AdoRelationUpdates
{
    [JsonPropertyName("added")]
    public List<AdoRelation>? Added { get; set; }

    [JsonPropertyName("removed")]
    public List<AdoRelation>? Removed { get; set; }

    [JsonPropertyName("updated")]
    public List<AdoRelation>? Updated { get; set; }
}

internal sealed class AdoUpdateIdentity
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
