using System.Globalization;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// Outcome of an interactive auth flow. Either a populated <see cref="TwigRefreshTokenStoreEntry"/>
/// ready to be persisted, or a structured error explaining what went wrong (so the command
/// surface can render specific guidance — "policy blocks device code", "browser launch
/// failed, copy this URL", etc.).
/// </summary>
internal sealed record InteractiveAuthResult
{
    public TwigRefreshTokenStoreEntry? Entry { get; init; }
    public InteractiveAuthErrorKind ErrorKind { get; init; } = InteractiveAuthErrorKind.None;
    public string? ErrorMessage { get; init; }

    public bool Succeeded => Entry is not null;

    public static InteractiveAuthResult Success(TwigRefreshTokenStoreEntry entry)
        => new() { Entry = entry };

    internal static InteractiveAuthResult FromTokens(
        AuthCodeTokenResponse tokens, string clientId, string tenant, string source)
    {
        var claims = IdTokenDecoder.Decode(tokens.IdToken);
        var enrollmentTenant = claims.TenantId ?? tenant;
        var enrollmentObject = claims.ObjectId;
        var accountName = claims.UserPrincipalName;
        if (string.IsNullOrWhiteSpace(claims.TenantId) && tenant.Contains('.')
            && Uri.CheckHostName(tenant) == UriHostNameType.Dns)
        {
            // The domain selects the authority; only authenticated issuance supplies its tenant ID.
            var accessClaims = JwtAccessTokenInspector.TryDecode(tokens.AccessToken);
            if (accessClaims is not { IsValidAdoAudience: true }
                || !Guid.TryParse(accessClaims.TenantId, out var issuedTenant)
                || string.IsNullOrWhiteSpace(accessClaims.ObjectId))
                return Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                    "Token exchange did not supply an Azure DevOps access token with tenant ID and oid claims for the requested tenant domain.");
            enrollmentTenant = issuedTenant.ToString("D");
            if (!string.IsNullOrWhiteSpace(enrollmentObject)
                && !string.Equals(enrollmentObject, accessClaims.ObjectId, StringComparison.OrdinalIgnoreCase))
                return Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                    "The sign-in tokens have different oid claims.");
            enrollmentObject = accessClaims.ObjectId;
            accountName ??= accessClaims.AccountName;
        }
        if (Guid.TryParse(tenant, out var requestedTenant)
            && (!Guid.TryParse(enrollmentTenant, out var resolvedTenant) || requestedTenant != resolvedTenant))
            return Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                "The requested tenant ID and sign-in token have different tenant claims.");

        return Success(new TwigRefreshTokenStoreEntry
        {
            RefreshToken = tokens.RefreshToken,
            ClientId = clientId,
            TenantId = enrollmentTenant,
            AuthorityHost = AuthorizeRequestBuilder.DefaultAuthorityHost,
            UserPrincipalName = accountName,
            ObjectId = enrollmentObject,
            BootstrappedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Source = source,
        });
    }

    public static InteractiveAuthResult Failure(InteractiveAuthErrorKind kind, string message)
        => new() { ErrorKind = kind, ErrorMessage = message };
}

internal enum InteractiveAuthErrorKind
{
    None = 0,

    /// <summary>The local HttpListener could not bind any port.</summary>
    LoopbackUnavailable,

    /// <summary>The OS browser could not be launched.</summary>
    BrowserLaunchFailed,

    /// <summary>The user closed the browser without completing the flow, or our timeout fired.</summary>
    Timeout,

    /// <summary>The state parameter did not round-trip — possible CSRF or tampered redirect.</summary>
    StateMismatch,

    /// <summary>AAD returned an error in the redirect (consent denied, policy block, etc.).</summary>
    AuthorizationServerError,

    /// <summary>The token endpoint POST failed (network, server error, malformed response).</summary>
    TokenExchangeFailed,

    /// <summary>The flow is blocked by tenant policy (e.g. device code disabled).</summary>
    PolicyBlocked,

    /// <summary>Caller cancelled before the flow completed.</summary>
    Cancelled,
}
