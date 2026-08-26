using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// Resolves the authenticated ADO holder (identity + display name) from the
/// existing connection surface. AB#737 §Cross-cutting rules requires
/// authorization never be inferred from OS username, a config default, or
/// any other ambient identity: the resolver reads the connection's
/// authenticated user via
/// <see cref="IIterationService.GetAuthenticatedUserDisplayNameAsync"/>
/// and returns <see cref="ClaimHolderDescriptor"/> only when that surface
/// yields a non-empty identity. Every other outcome — network failure,
/// authentication failure, or an empty response — surfaces as a
/// <see cref="Result"/> failure so the caller reports
/// <c>HolderUnavailable</c> instead of minting under a stale or fabricated
/// display name.
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

    public ConnectionHolderResolver(IIterationService iteration)
    {
        _iteration = iteration;
    }

    public async Task<Result<ClaimHolderDescriptor>> ResolveAsync(CancellationToken ct = default)
    {
        string? displayName;
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
            // AB#737 §Cross-cutting rules: authorization MUST NOT fall back
            // to a configured display name, OS username, or any other
            // ambient identity. An auth/network failure is
            // HolderUnavailable — a mint that reaches this branch has
            // observed the runtime connection identity as unresolvable and
            // MUST refuse.
            return Result.Fail<ClaimHolderDescriptor>($"holder-resolver-unavailable: {ex.GetType().Name}: {SanitizeMessage(ex.Message)}");
        }

        if (string.IsNullOrWhiteSpace(displayName))
            return Result.Fail<ClaimHolderDescriptor>("holder-resolver-empty");

        // The connection reports one authoritative name; use it verbatim as
        // both identity and display so downstream ADO writes and local
        // storage round-trip byte-identically.
        return Result.Ok(new ClaimHolderDescriptor(displayName, displayName));
    }

    private static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "unknown";
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
