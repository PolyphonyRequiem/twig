using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// Resolves the authenticated ADO holder from the connection surface.
/// AB#737 §Cross-cutting rules requires authorization never be inferred
/// from OS username, a config default, or any other ambient identity: the
/// resolver reads the connection's authenticated identity via
/// <see cref="IIterationService.GetAuthenticatedUserIdentityAsync"/> and
/// returns a <see cref="ClaimHolderDescriptor"/> only when both the
/// display name AND the stable <c>uniqueName</c> come back non-empty.
/// A resolver that cannot supply <c>uniqueName</c> refuses to mint —
/// AB#739's readback verification relies on comparing the stable
/// identity byte-exactly, and any string that "matches by display" but
/// carries a different UPN would silently pass.
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
        (string? DisplayName, string? UniqueName) identity;
        try
        {
            identity = await _iteration.GetAuthenticatedUserIdentityAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Fail<ClaimHolderDescriptor>($"holder-resolver-unavailable: {ex.GetType().Name}: {SanitizeMessage(ex.Message)}");
        }

        if (string.IsNullOrWhiteSpace(identity.UniqueName))
            return Result.Fail<ClaimHolderDescriptor>("holder-resolver-empty-unique-name");
        if (string.IsNullOrWhiteSpace(identity.DisplayName))
            return Result.Fail<ClaimHolderDescriptor>("holder-resolver-empty-display-name");

        // Identity is the stable value the claim record persists — use the
        // uniqueName so downstream comparisons (validation, projection
        // readback) key on a stable token, not a rendering.
        return Result.Ok(new ClaimHolderDescriptor(
            Identity: identity.UniqueName!,
            DisplayName: identity.DisplayName));
    }

    private static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "unknown";
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
