using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Centralizes auth provider construction so every entry point (CLI, MCP server)
/// produces the same provider chain for a given auth method.
/// </summary>
internal static class AuthProviderFactory
{
    /// <summary>
    /// Creates an <see cref="IAuthenticationProvider"/> for the specified auth method.
    /// </summary>
    /// <param name="authMethod">
    /// The configured auth method (e.g. <c>"pat"</c>, <c>"azcli"</c>).
    /// Case-insensitive. Any value other than <c>"pat"</c> falls through to the
    /// MSAL-cache-first AzCli chain.
    /// </param>
    /// <returns>A fully-composed authentication provider.</returns>
    public static IAuthenticationProvider Create(string authMethod)
    {
        if (string.Equals(authMethod, "pat", StringComparison.OrdinalIgnoreCase))
            return new PatAuthProvider();

        return new MsalCacheTokenProvider(new AzCliAuthProvider());
    }
}
