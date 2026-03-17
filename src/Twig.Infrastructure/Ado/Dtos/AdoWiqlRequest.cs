using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// Request body for WIQL queries.
/// </summary>
internal sealed class AdoWiqlRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
}
