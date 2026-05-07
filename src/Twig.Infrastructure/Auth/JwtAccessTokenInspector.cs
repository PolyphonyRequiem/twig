using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// JWT payload fields we care about for ADO access token validation and diagnostics.
/// Source-generated JSON; no reflection (AOT-safe).
/// </summary>
internal sealed class JwtAccessTokenPayload
{
    /// <summary>Audience claim — must equal the ADO resource ID for ADO API calls.</summary>
    [JsonPropertyName("aud")]
    public string? Audience { get; set; }

    /// <summary>Application ID — typically the client that requested the token (Azure CLI, etc.).</summary>
    [JsonPropertyName("appid")]
    public string? AppId { get; set; }

    /// <summary>Expiration time (Unix epoch seconds).</summary>
    [JsonPropertyName("exp")]
    public long? ExpiresAtUnix { get; set; }

    /// <summary>Issued-at time (Unix epoch seconds).</summary>
    [JsonPropertyName("iat")]
    public long? IssuedAtUnix { get; set; }

    /// <summary>Tenant ID.</summary>
    [JsonPropertyName("tid")]
    public string? TenantId { get; set; }

    /// <summary>User principal name (e.g. user@contoso.com). May be null for service principals.</summary>
    [JsonPropertyName("upn")]
    public string? UserPrincipalName { get; set; }

    /// <summary>Object ID of the principal.</summary>
    [JsonPropertyName("oid")]
    public string? ObjectId { get; set; }

    /// <summary>Issuer — the AAD authority that minted the token.</summary>
    [JsonPropertyName("iss")]
    public string? Issuer { get; set; }
}

/// <summary>
/// Snapshot of an inspected JWT — the parsed payload plus computed fields.
/// </summary>
internal sealed record JwtTokenInfo(
    string? Audience,
    string? AppId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? IssuedAt,
    string? TenantId,
    string? UserPrincipalName,
    string? ObjectId,
    string? Issuer)
{
    /// <summary>True iff the token's audience claim matches the ADO API resource.</summary>
    public bool IsValidAdoAudience
        => Audience is { Length: > 0 }
           && (Audience.Equals(JwtAccessTokenInspector.AdoResourceId, StringComparison.OrdinalIgnoreCase)
               || Audience.Equals(JwtAccessTokenInspector.AdoResourceUri, StringComparison.OrdinalIgnoreCase));

    /// <summary>True iff the token has not expired (with a small buffer).</summary>
    public bool IsNotExpired(DateTimeOffset now, TimeSpan buffer)
        => ExpiresAt is { } exp && exp > now + buffer;
}

/// <summary>
/// Decodes an Azure AD JWT access token and surfaces the claims we care about.
/// Pure utility — no I/O, no exceptions on malformed input (returns null instead).
/// AOT-safe: Base64Url + source-generated JSON only.
/// </summary>
internal static class JwtAccessTokenInspector
{
    /// <summary>The Azure DevOps API resource ID (audience claim format).</summary>
    public const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>Some token issuers use the URI form for the audience claim.</summary>
    public const string AdoResourceUri = "https://app.vssps.visualstudio.com/";

    /// <summary>
    /// Attempts to decode the JWT payload. Returns null for any non-JWT input
    /// (PAT/Basic strings, opaque tokens, malformed JWTs, etc.) — never throws.
    /// </summary>
    public static JwtTokenInfo? TryDecode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        // Strip an optional "Bearer " prefix; ApplyAuthHeader does not store it
        // but defensive parsing makes this usable from diagnostics paths too.
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        // PAT / Basic auth values are NOT JWTs — bail early.
        if (token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        // A JWT has exactly three Base64Url segments separated by '.'
        var firstDot = token.IndexOf('.');
        if (firstDot <= 0) return null;
        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot <= firstDot + 1) return null;
        if (token.IndexOf('.', secondDot + 1) >= 0) return null;

        var payloadSegment = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);

        byte[]? payloadBytes;
        try
        {
            payloadBytes = DecodeBase64Url(payloadSegment);
        }
        catch
        {
            return null;
        }
        if (payloadBytes is null) return null;

        JwtAccessTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(payloadBytes, TwigJsonContext.Default.JwtAccessTokenPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null) return null;

        return new JwtTokenInfo(
            Audience: payload.Audience,
            AppId: payload.AppId,
            ExpiresAt: payload.ExpiresAtUnix is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp) : null,
            IssuedAt: payload.IssuedAtUnix is { } iat ? DateTimeOffset.FromUnixTimeSeconds(iat) : null,
            TenantId: payload.TenantId,
            UserPrincipalName: payload.UserPrincipalName,
            ObjectId: payload.ObjectId,
            Issuer: payload.Issuer);
    }

    /// <summary>
    /// Convenience: returns true iff the token is a JWT whose audience is the ADO API.
    /// Returns false for non-JWT tokens (PATs, malformed strings) since callers
    /// validate audience as a filter on REST refresher / MSAL cache results,
    /// neither of which can return a PAT.
    /// </summary>
    public static bool HasValidAdoAudience(string? token)
        => TryDecode(token) is { IsValidAdoAudience: true };

    /// <summary>
    /// Decodes a Base64Url segment (no padding, '-'/'_' instead of '+'/'/').
    /// Returns null if the input is not valid Base64Url.
    /// </summary>
    private static byte[]? DecodeBase64Url(ReadOnlySpan<char> source)
    {
        // Convert Base64Url -> Base64 with padding by allocating once.
        var padding = (4 - source.Length % 4) % 4;
        Span<char> buffer = source.Length + padding <= 4096
            ? stackalloc char[source.Length + padding]
            : new char[source.Length + padding];

        for (var i = 0; i < source.Length; i++)
        {
            buffer[i] = source[i] switch
            {
                '-' => '+',
                '_' => '/',
                _ => source[i],
            };
        }
        for (var i = 0; i < padding; i++)
            buffer[source.Length + i] = '=';

        try
        {
            return Convert.FromBase64CharArray(buffer.ToArray(), 0, buffer.Length);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a privacy-safe one-line summary suitable for diagnostic output.
    /// Deliberately omits the token itself; only includes claim metadata.
    /// </summary>
    public static string DescribeForDiagnostics(JwtTokenInfo info, DateTimeOffset now)
    {
        var audLabel = info.Audience switch
        {
            null => "(none)",
            AdoResourceId => $"{AdoResourceId} (ADO ✓)",
            AdoResourceUri => $"{AdoResourceUri} (ADO ✓)",
            _ => $"{info.Audience} (NOT ADO ✗)",
        };

        var expLabel = info.ExpiresAt is { } exp
            ? $"{exp.ToString("u", CultureInfo.InvariantCulture)} ({FormatRelative(exp - now)})"
            : "(unknown)";

        return $"audience: {audLabel}\nexpires: {expLabel}\ntenant:  {info.TenantId ?? "(unknown)"}\nupn:     {info.UserPrincipalName ?? "(none)"}\nappid:   {info.AppId ?? "(unknown)"}";
    }

    private static string FormatRelative(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            return $"expired {FormatDuration(-delta)} ago";
        return $"in {FormatDuration(delta)}";
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{(int)d.TotalSeconds}s";
    }
}
