using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// A single JSON Patch operation for ADO work item create/update.
/// </summary>
internal sealed class AdoPatchOperation
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "add";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonNode? Value { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }
}
