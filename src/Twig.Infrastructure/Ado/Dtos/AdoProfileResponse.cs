using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for <c>GET https://app.vssps.visualstudio.com/_apis/profile/profiles/me</c>.
/// More reliable than connectionData for detecting the authenticated user's display name.
/// </summary>
internal sealed class AdoProfileResponse
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
