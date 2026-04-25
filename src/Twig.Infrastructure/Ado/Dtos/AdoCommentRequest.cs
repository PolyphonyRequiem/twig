using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// Request body for adding a comment to a work item.
/// </summary>
internal sealed class AdoCommentRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
