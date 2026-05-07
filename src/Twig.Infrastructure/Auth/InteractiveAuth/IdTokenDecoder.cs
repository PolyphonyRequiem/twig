using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// Decodes an OIDC <c>id_token</c> to extract identity stamps (upn, oid, tid) for the
/// refresh-token store entry. Does NOT verify the signature — we trust the token because
/// we just received it over TLS from AAD's <c>/token</c> endpoint in response to our own
/// PKCE-protected request. This is decode-for-display, not authentication.
/// </summary>
internal static class IdTokenDecoder
{
    public sealed record IdTokenClaims(string? UserPrincipalName, string? ObjectId, string? TenantId);

    public static IdTokenClaims Decode(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return new IdTokenClaims(null, null, null);

        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return new IdTokenClaims(null, null, null);

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            var payload = JsonSerializer.Deserialize(
                Encoding.UTF8.GetString(payloadBytes),
                TwigJsonContext.Default.JwtAccessTokenPayload);

            if (payload is null)
                return new IdTokenClaims(null, null, null);

            return new IdTokenClaims(
                payload.UserPrincipalName,
                payload.ObjectId,
                payload.TenantId);
        }
        catch
        {
            return new IdTokenClaims(null, null, null);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
