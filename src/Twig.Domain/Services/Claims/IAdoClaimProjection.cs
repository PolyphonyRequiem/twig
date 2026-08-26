using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Domain.Services.Claims;

/// <summary>
/// Abstract seam for projecting the local claim onto the visible ADO surface
/// (AB#737 §Interface — <c>AdoProjectionBinding</c>). Consumed exclusively by
/// mint/reclaim/release and expressing exactly the two operations the claim
/// lifecycle needs — <see cref="ProjectHolderAsync"/> and
/// <see cref="ClearHolderAsync"/>. No other ADO surface is touched by the
/// claim lifecycle.
/// <para>
/// The seam is intentionally process-agnostic: it takes an opaque
/// <c>primaryScopeId</c> (rendered work-item id) and an opaque <c>holder</c>
/// descriptor. It never inspects work-item type, state, template, or
/// backlog position; a concrete implementation targets exactly
/// <c>System.AssignedTo</c> because #728 fixes that as the ADO responsibility
/// signal projected by claim mint/release, and only that.
/// </para>
/// <para>
/// Every failure surfaces as a <see cref="Result"/> error carrying a short
/// underlying reason (auth, network, ADO rule refusal, concurrency conflict).
/// The mint/reclaim path maps every failure to
/// <see cref="ClaimMintOutcome.AdoProjectionFailed"/> and terminalizes the
/// pending row as <c>mint-abort</c> before returning; the release path maps
/// every failure to <see cref="ClaimReleaseOutcome.ReleaseAdoProjectionFailed"/>
/// and leaves the row <see cref="ClaimStates.Active"/>.
/// </para>
/// </summary>
internal interface IAdoClaimProjection
{
    /// <summary>Project the given holder onto the ADO responsibility signal
    /// for the primary scope. The primary scope id is the opaque rendered
    /// identifier from <see cref="ClaimRecord.PrimaryScopeId"/>; the concrete
    /// implementation parses it into the native work-item id.</summary>
    Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default);

    /// <summary>Clear the ADO responsibility signal for the primary scope. A
    /// success return means ADO now shows no active responsibility on the
    /// scope — the release path relies on this to sequence local
    /// terminalization after ADO acknowledges the clear.</summary>
    Task<Result> ClearHolderAsync(string primaryScopeId, CancellationToken ct = default);
}

/// <summary>
/// Opaque holder descriptor forwarded to the ADO projection. Carries both the
/// stable identity captured in the claim record (<see cref="Identity"/>) and
/// the resolved display form the projection SHOULD write into
/// <c>System.AssignedTo</c> (<see cref="DisplayName"/>). Both are supplied by
/// the runtime resolver (<see cref="IClaimHolderResolver"/>); neither is
/// derived from OS username or an in-service default.
/// </summary>
internal sealed record ClaimHolderDescriptor(string Identity, string? DisplayName);
