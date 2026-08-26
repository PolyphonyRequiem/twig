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
/// Opaque holder descriptor forwarded to the ADO projection. Carries three
/// values captured at mint time:
/// <list type="bullet">
///   <item><see cref="Identity"/>: the stable identity string persisted in
///     the claim record. Historically the display name; AB#739's readback
///     verification uses <see cref="UniqueName"/> instead so identity is
///     compared against ADO's stable representation, not a rendering.</item>
///   <item><see cref="DisplayName"/>: the human-readable rendering.
///     Persisted for status projections and log formatting only; never a
///     comparison key.</item>
///   <item><see cref="UniqueName"/>: the stable ADO identity (UPN or
///     descriptor). ADO's <c>System.AssignedTo</c> accepts a UPN verbatim
///     as the write value and reflects it back on read as the
///     <c>uniqueName</c> field. The claim projection writes this string
///     and byte-compares it on readback; a resolver that cannot supply
///     one refuses to mint (<c>HolderUnavailable</c>). Nullable at the
///     shape level so downstream code compiles; the resolver enforces
///     non-null.</item>
/// </list>
/// </summary>
internal sealed record ClaimHolderDescriptor(string Identity, string? DisplayName, string? UniqueName = null);
