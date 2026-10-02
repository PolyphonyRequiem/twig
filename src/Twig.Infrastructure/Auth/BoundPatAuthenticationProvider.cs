using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Frozen PAT principal requirements, with admission proof tied to the exact secret
/// used for the request. Replacing bytes at an unchanged reference cannot borrow proof.
/// </summary>
internal sealed class BoundPatAuthenticationProvider(
    AuthenticationIdentity identity,
    PatCredentialStore store,
    PatPrincipalAttestor attestor) : IAuthenticationProvider, IBoundAuthenticationMetadata
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _verifiedPat;
    private string? _verifiedReset;
    private string? _verifiedAuthorization;

    public Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(identity);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pat = store.Read();
            var reset = store.ReadResetStamp();
            if (!string.Equals(_verifiedPat, pat, StringComparison.Ordinal)
                || !string.Equals(_verifiedReset, reset, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(identity.AdoAuthority) || string.IsNullOrWhiteSpace(identity.AdoPrincipalId))
                    throw new AdoAuthenticationException("Selected PAT identity has incomplete principal evidence; explicit enrollment is required.");
                var principal = await attestor.AttestAsync(identity.AdoAuthority, pat, ct).ConfigureAwait(false);
                if (!string.Equals(principal.PrincipalId, identity.AdoPrincipalId, StringComparison.OrdinalIgnoreCase))
                    throw new ConnectionIdentityMismatchException(
                        $"PAT identity '{identity.Name}' requires ADO principal {identity.AdoPrincipalId} at {identity.AdoAuthority}; observed principal {principal.PrincipalId}. Repair the selected identity with 'twig auth pat' or register a separate identity. No work request was admitted.");
                if (!string.Equals(store.Read(), pat, StringComparison.Ordinal)
                    || !string.Equals(store.ReadResetStamp(), reset, StringComparison.Ordinal))
                    throw new AdoAuthenticationException("Selected PAT changed during principal attestation; repeat the operation to verify current material.");
                _verifiedAuthorization = PatPrincipalAttestor.FormatAuthorization(pat);
                _verifiedPat = pat;
                _verifiedReset = reset;
            }
            return _verifiedAuthorization!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidateToken() => store.ResetAdmission();
}
