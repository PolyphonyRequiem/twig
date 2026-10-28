using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Domain.Services.Claims;

/// <summary>
/// Projects the local claim onto the selected provider's responsibility surface.
/// Consumed exclusively by mint/reclaim/release and expressing exactly the two operations the claim
/// lifecycle needs — <see cref="ProjectHolderAsync"/> and
/// <see cref="ClearHolderAsync"/>. No other provider surface is touched by the
/// claim lifecycle.
/// <para>
/// The seam is intentionally process-agnostic: it takes an opaque
/// <c>primaryScopeId</c> (rendered work-item id) and an opaque <c>holder</c>
/// descriptor. It never inspects work-item type, state, template, or
/// backlog position. The adapter owns the native field mapping and identity verification.
/// </para>
/// <para>
/// Every failure surfaces as a <see cref="Result"/> error carrying a short
/// underlying reason (auth, network, provider rule refusal, concurrency conflict).
/// The mint/reclaim path maps every failure to
/// <see cref="ClaimMintOutcome.ProjectionFailed"/> and terminalizes the
/// pending row as <c>mint-abort</c> before returning; the release path maps
/// every failure to <see cref="ClaimReleaseOutcome.ReleaseProjectionFailed"/>
/// and leaves the row <see cref="ClaimStates.Active"/>.
/// </para>
/// </summary>
internal interface IClaimProjection
{
    /// <summary>Project the given holder onto the provider responsibility signal
    /// for the primary scope. The primary scope id is the opaque rendered
    /// identifier from <see cref="ClaimRecord.PrimaryScopeId"/>; the concrete
    /// implementation resolves it into the native work-item identity.</summary>
    Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default);

    /// <summary>Clear the provider responsibility signal before local terminalization.
    /// Success must mean the provider acknowledged the clear.</summary>
    Task<Result> ClearHolderAsync(string primaryScopeId, CancellationToken ct = default);
}

/// <summary>
/// Opaque holder descriptor forwarded to the provider projection. Carries two
/// values captured at mint time:
/// <list type="bullet">
///   <item><see cref="Identity"/>: the stable identity string persisted in
///     the claim record. AB#739's readback verification requires the
///     resolver to supply the stable provider identity here, not a display
///     rendering. The adapter verifies the observed stable identity against it.</item>
///   <item><see cref="DisplayName"/>: the human-readable rendering.
///     Persisted for status projections and log formatting only; never a
///     comparison key.</item>
/// </list>
/// </summary>
internal sealed record ClaimHolderDescriptor(string Identity, string? DisplayName);
