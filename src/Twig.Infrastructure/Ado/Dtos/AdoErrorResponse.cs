using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API error response body.
/// </summary>
internal sealed class AdoErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("typeKey")]
    public string? TypeKey { get; set; }
}
