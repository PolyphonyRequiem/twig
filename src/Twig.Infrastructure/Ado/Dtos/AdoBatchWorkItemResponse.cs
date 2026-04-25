using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for batch work item fetch (GET workitems?ids=...).
/// </summary>
internal sealed class AdoBatchWorkItemResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoWorkItemResponse>? Value { get; set; }
}
