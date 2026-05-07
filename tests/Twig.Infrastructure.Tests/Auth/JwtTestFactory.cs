using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Builds compact JWT strings (header.payload.signature) for testing.
/// The signature is a placeholder — the inspector under test never validates signatures,
/// only decodes the payload.
/// </summary>
internal static class JwtTestFactory
{
    public const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";
    public const string AdoResourceUri = "https://app.vssps.visualstudio.com/";

    /// <summary>
    /// Builds a JWT with the supplied payload claims. Pass <paramref name="audience"/> = null
    /// to omit the aud claim entirely.
    /// </summary>
    public static string Build(
        string? audience = AdoResourceId,
        DateTimeOffset? expiresAt = null,
        string? tenantId = "ten-1234",
        string? upn = "user@contoso.com",
        string? appId = "test-app",
        string? issuer = "https://sts.windows.net/ten-1234/")
    {
        expiresAt ??= DateTimeOffset.UtcNow.AddHours(1);

        var header = """{"alg":"none","typ":"JWT"}""";
        var payload = new Dictionary<string, object?>();
        if (audience is not null) payload["aud"] = audience;
        payload["exp"] = expiresAt.Value.ToUnixTimeSeconds();
        payload["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (tenantId is not null) payload["tid"] = tenantId;
        if (upn is not null) payload["upn"] = upn;
        if (appId is not null) payload["appid"] = appId;
        if (issuer is not null) payload["iss"] = issuer;

        var payloadJson = JsonSerializer.Serialize(payload);

        return $"{Base64UrlEncode(header)}.{Base64UrlEncode(payloadJson)}.signature-placeholder";
    }

    /// <summary>
    /// Builds a JWT for the wrong audience (e.g. management.azure.com) — used to verify
    /// that wrong-audience tokens are rejected even when other metadata looks plausible.
    /// </summary>
    public static string BuildWrongAudience(DateTimeOffset? expiresAt = null)
        => Build(audience: "https://management.azure.com/", expiresAt: expiresAt);

    private static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Builds the MSAL cache JSON string with a single ADO access token entry.
    /// </summary>
    public static string BuildMsalCacheJson(
        string secret,
        string target = AdoResourceId + "/.default",
        long? expiresOnEpoch = null)
    {
        expiresOnEpoch ??= DateTimeOffset.UtcNow.AddMinutes(50).ToUnixTimeSeconds();
        return $$"""
        {
            "AccessToken": {
                "entry1": {
                    "secret": "{{secret}}",
                    "target": "{{target}}",
                    "expires_on": "{{expiresOnEpoch.Value.ToString(CultureInfo.InvariantCulture)}}"
                }
            }
        }
        """;
    }

    /// <summary>
    /// Builds an MSAL cache JSON string containing a RefreshToken + Account pair sufficient
    /// for <c>MsalTokenRefresher.FindRefreshContext</c> to extract a bootstrap context.
    /// </summary>
    public static string BuildMsalCacheJsonWithRefreshToken(
        string refreshTokenSecret = "rt-secret",
        string clientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
        string tenantId = "ten-1234",
        string oid = "oid-5678",
        string authorityHost = "login.microsoftonline.com")
    {
        var homeAccountId = $"{oid}.{tenantId}";
        return $$"""
        {
            "RefreshToken": {
                "rt1": {
                    "secret": "{{refreshTokenSecret}}",
                    "client_id": "{{clientId}}",
                    "home_account_id": "{{homeAccountId}}"
                }
            },
            "Account": {
                "acct1": {
                    "home_account_id": "{{homeAccountId}}",
                    "realm": "{{tenantId}}",
                    "environment": "{{authorityHost}}"
                }
            }
        }
        """;
    }
}
