using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// Resolves the authenticated ADO holder (identity + display name) from the
/// existing connection surface. AB#737 §Cross-cutting rules requires
/// authorization never be inferred from OS username or an ambient default;
/// the resolver reads the connection's authenticated user via
/// <see cref="IIterationService.GetAuthenticatedUserDisplayNameAsync"/> and,
/// when supplied, falls back to the workspace configuration's
/// <c>User.DisplayName</c> — the canonical holder value <c>twig sync</c>
/// already uses to key the workspace projection.
/// <para>
/// The resolver runs at mint time and its result is captured verbatim into
/// the claim record. Downstream validation never re-resolves — a stored
/// <c>holderIdentity</c> is compared against the row itself, not the current
/// resolved identity.
/// </para>
/// </summary>
internal sealed class ConnectionHolderResolver : IClaimHolderResolver
{
    private readonly IIterationService _iteration;
    private readonly TwigConfiguration _config;

    public ConnectionHolderResolver(IIterationService iteration, TwigConfiguration config)
    {
        _iteration = iteration;
        _config = config;
    }

    public async Task<Result<ClaimHolderDescriptor>> ResolveAsync(CancellationToken ct = default)
    {
        string? displayName = null;
        try
        {
            displayName = await _iteration.GetAuthenticatedUserDisplayNameAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network / auth failures fall back to the configured default so
            // an offline mint can still capture a stable holder identity
            // AB#737 §Cross-cutting rules names as authoritative.
            _ = ex;
        }

        // Preferred identity precedence: authenticated user → configured
        // user → nothing (fail-loud).
        var configured = _config.User?.DisplayName;
        var identity = !string.IsNullOrWhiteSpace(displayName) ? displayName
            : !string.IsNullOrWhiteSpace(configured) ? configured
            : null;
        if (string.IsNullOrWhiteSpace(identity))
            return Result.Fail<ClaimHolderDescriptor>("no authenticated holder available.");
        var display = !string.IsNullOrWhiteSpace(displayName) ? displayName : identity;
        return Result.Ok(new ClaimHolderDescriptor(identity!, display));
    }
}
