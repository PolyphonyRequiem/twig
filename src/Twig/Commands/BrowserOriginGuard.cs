using Twig.Infrastructure.Auth;

namespace Twig.Commands;

/// <summary>Expected origins are preconditions, never authentication selectors or fallback identities.</summary>
internal static class BrowserOriginGuard
{
    internal static void EnsureExpected(ResolvedConnectionBinding binding, string? expectBinding, string? expectIdentity)
    {
        if ((expectBinding is not null && !string.Equals(expectBinding, binding.Binding.BindingId, StringComparison.Ordinal))
            || (expectIdentity is not null && !string.Equals(expectIdentity, binding.Identity.IdentityId, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "The browser connection or identity changed. Reconnect before retrying; no Bench changes were made.");
    }
}
