using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for listing work item types.
/// </summary>
internal sealed class AdoWorkItemTypeListResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoWorkItemTypeResponse>? Value { get; set; }
}

/// <summary>
/// A single work item type definition from ADO.
/// </summary>
internal sealed class AdoWorkItemTypeResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("icon")]
    public AdoWorkItemTypeIconResponse? Icon { get; set; }

    [JsonPropertyName("isDisabled")]
    public bool IsDisabled { get; set; }

    [JsonPropertyName("states")]
    public List<AdoWorkItemStateColor>? States { get; set; }
}

/// <summary>
/// Icon metadata for an ADO work item type.
/// </summary>
internal sealed class AdoWorkItemTypeIconResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// State entry within a work item type response (from GET /_apis/wit/workitemtypes).
/// The field is named "category" on the classic WIT endpoint (not "stateCategory").
/// </summary>
internal sealed class AdoWorkItemStateColor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>State category: Proposed, InProgress, Resolved, Completed, Removed.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }
}
