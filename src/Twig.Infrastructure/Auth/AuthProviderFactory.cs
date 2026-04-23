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
    /// Case-insensitive; any non-<c>"pat"</c> value returns the MSAL-cache-first AzCli chain.
    /// </summary>
    public static IAuthenticationProvider Create(string authMethod)
    {
        if (string.Equals(authMethod, "pat", StringComparison.OrdinalIgnoreCase))
            return new PatAuthProvider();

        return new MsalCacheTokenProvider(new AzCliAuthProvider());
    }
}
