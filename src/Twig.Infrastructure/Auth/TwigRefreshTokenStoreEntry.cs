using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// On-disk schema for <c>~/.twig/.refresh-token</c>. Owned entirely by twig — never
/// touched by az CLI or any other process. Bootstrapped once from the MSAL cache,
/// then sealed off (subsequent reads come exclusively from this file).
/// </summary>
internal sealed class TwigRefreshTokenStoreEntry
{
    /// <summary>The OAuth2 refresh token secret. Never logged; stored chmod 600 on Unix.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>The OAuth2 client ID this refresh token is bound to (e.g. Azure CLI's well-known ID).</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>The AAD tenant (realm) the refresh token is scoped to.</summary>
    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }

    /// <summary>The AAD authority host (e.g. <c>login.microsoftonline.com</c>).</summary>
    [JsonPropertyName("authority_host")]
    public string? AuthorityHost { get; set; }

    /// <summary>UPN of the identity the refresh token is bound to (recorded for diagnostics + identity guard).</summary>
    [JsonPropertyName("upn")]
    public string? UserPrincipalName { get; set; }

    /// <summary>Object ID of the identity (immutable AAD identifier, fallback when UPN is absent).</summary>
    [JsonPropertyName("oid")]
    public string? ObjectId { get; set; }

    /// <summary>UTC timestamp (ISO 8601) of when this entry was bootstrapped.</summary>
    [JsonPropertyName("bootstrapped_at")]
    public string? BootstrappedAt { get; set; }

    /// <summary>Source of the bootstrap (currently always <c>"azcli"</c>; reserved for future <c>"device-code"</c>).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
