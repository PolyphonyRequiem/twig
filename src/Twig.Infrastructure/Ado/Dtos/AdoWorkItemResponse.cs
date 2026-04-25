using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for a single work item.
/// </summary>
internal sealed class AdoWorkItemResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("rev")]
    public int Rev { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, object?>? Fields { get; set; }

    [JsonPropertyName("relations")]
    public List<AdoRelation>? Relations { get; set; }
}

/// <summary>
/// A relation link on a work item (parent, child, related, etc.).
/// </summary>
internal sealed class AdoRelation
{
    [JsonPropertyName("rel")]
    public string? Rel { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; set; }
}
