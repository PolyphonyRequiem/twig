using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for <c>GET {org}/_apis/connectionData</c>.
/// Used to detect the authenticated user's display name.
/// </summary>
internal sealed class AdoConnectionDataResponse
{
    [JsonPropertyName("authenticatedUser")]
    public AdoAuthenticatedUser? AuthenticatedUser { get; set; }
}

internal sealed class AdoAuthenticatedUser
{
    [JsonPropertyName("providerDisplayName")]
    public string? ProviderDisplayName { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
