using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for a WIQL query.
/// </summary>
internal sealed class AdoWiqlResponse
{
    [JsonPropertyName("queryType")]
    public string? QueryType { get; set; }

    [JsonPropertyName("workItems")]
    public List<AdoWiqlWorkItemRef>? WorkItems { get; set; }
}

/// <summary>
/// Minimal work item reference returned by WIQL queries.
/// </summary>
internal sealed class AdoWiqlWorkItemRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
