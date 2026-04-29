using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for classification nodes (areas/iterations).
/// Endpoint: GET {org}/{project}/_apis/wit/classificationnodes/areas?$depth=10
/// </summary>
internal sealed class AdoClassificationNodeResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("structureType")]
    public string? StructureType { get; set; }

    [JsonPropertyName("hasChildren")]
    public bool HasChildren { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("children")]
    public List<AdoClassificationNodeResponse>? Children { get; set; }
}
