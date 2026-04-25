using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for team iterations.
/// </summary>
internal sealed class AdoIterationListResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoIterationResponse>? Value { get; set; }
}

/// <summary>
/// A single iteration (sprint) entry from ADO.
/// </summary>
internal sealed class AdoIterationResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("attributes")]
    public AdoIterationAttributes? Attributes { get; set; }
}

/// <summary>
/// Iteration date attributes.
/// </summary>
internal sealed class AdoIterationAttributes
{
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("finishDate")]
    public string? FinishDate { get; set; }

    [JsonPropertyName("timeFrame")]
    public string? TimeFrame { get; set; }
}
