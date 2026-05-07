using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// Pure helpers for building AAD authorize URLs and validating callback responses.
/// Stateless — no I/O. Tested independently of the loopback listener and HTTP exchanger.
/// </summary>
internal static class AuthorizeRequestBuilder
{
    /// <summary>Default AAD authority. Sovereign clouds override via <c>authorityHost</c>.</summary>
    public const string DefaultAuthorityHost = "login.microsoftonline.com";

    /// <summary>
    /// Default tenant for <c>twig login</c>. <c>organizations</c> restricts to AAD work/school
    /// accounts — appropriate for ADO, which never accepts personal Microsoft accounts.
    /// Override with a specific tenant GUID/domain via the <c>--tenant</c> flag.
    /// </summary>
    public const string DefaultTenant = "organizations";

    /// <summary>
    /// ADO scope (<c>{resource}/.default</c>) plus <c>offline_access</c> so AAD issues a
    /// refresh token in the response. Without <c>offline_access</c> the response carries
    /// only an access token — fatal for our bootstrap-once architecture.
    /// </summary>
    public const string Scopes = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";

    /// <summary>
    /// Builds the full authorize URL for the authorization-code flow.
    /// </summary>
    public static string BuildAuthorizeUrl(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state,
        string tenant = DefaultTenant,
        string authorityHost = DefaultAuthorityHost,
        string scopes = Scopes)
    {
        var query = new StringBuilder();
        Append(query, "client_id", clientId);
        Append(query, "response_type", "code");
        Append(query, "redirect_uri", redirectUri);
        Append(query, "response_mode", "query");
        Append(query, "scope", scopes);
        Append(query, "code_challenge", codeChallenge);
        Append(query, "code_challenge_method", PkceCodes.ChallengeMethod);
        Append(query, "state", state);
        Append(query, "prompt", "select_account");

        return string.Create(CultureInfo.InvariantCulture,
            $"https://{authorityHost}/{tenant}/oauth2/v2.0/authorize?{query}");
    }

    /// <summary>
    /// Generates a cryptographically random opaque <c>state</c> value for CSRF protection
    /// across the redirect.
    /// </summary>
    public static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return PkceCodes.Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Constant-time comparison of two state values to prevent timing side channels.
    /// Returns false for any null/length-mismatch.
    /// </summary>
    public static bool ValidateState(string? expected, string? actual)
    {
        if (expected is null || actual is null) return false;
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(actual);
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0) sb.Append('&');
        sb.Append(key).Append('=').Append(WebUtility.UrlEncode(value));
    }
}
