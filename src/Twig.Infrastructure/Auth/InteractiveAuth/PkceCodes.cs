using System.Security.Cryptography;
using System.Text;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// PKCE (Proof Key for Code Exchange, RFC 7636) verifier/challenge pair for the
/// authorization-code flow. The verifier is a high-entropy random secret kept by the
/// client; the challenge is the SHA-256 hash sent up-front in the authorize request,
/// then the verifier is sent to the token endpoint to prove possession of the original.
/// </summary>
internal readonly record struct PkceCodes(string Verifier, string Challenge)
{
    public const string ChallengeMethod = "S256";

    /// <summary>
    /// Generates a fresh PKCE pair. Verifier is 64 bytes of CSPRNG entropy, base64url-encoded
    /// (no padding) — well within the RFC's 43–128 character limit. Challenge is the
    /// SHA-256 of the verifier's ASCII bytes, also base64url-encoded.
    /// </summary>
    public static PkceCodes Generate()
    {
        Span<byte> verifierBytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64UrlEncode(verifierBytes);

        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier), hashBytes);
        var challenge = Base64UrlEncode(hashBytes);

        return new PkceCodes(verifier, challenge);
    }

    /// <summary>RFC 4648 §5 base64url encoding without padding.</summary>
    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
