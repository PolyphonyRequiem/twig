using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Builds the selectors the DEFAULT Bench is created with: the sprint rule, plus one selector per
/// pin that still lives in the tracking file (docs/specs/bench.spec.md §3, §6).
/// <para>
/// 🔴 This exists so there is exactly ONE answer to "what does the default Bench start as".
/// <see cref="WorkingSetService"/> reads that answer when it computes the view, and the pin
/// workflow reads the same answer when a pin is the first thing that causes the Bench to be
/// created. A second copy would let the view and the write path disagree about what a fresh
/// default Bench holds — and the disagreement would only show up as a missing pin, silently.
/// </para>
/// <para>
/// 🔴 A fresh default Bench holds the SPRINT RULE AND NOTHING ELSE (ADO #146). It used to seed
/// itself from pins in the tracking file, because the file was the pin store and the two had to
/// coexist. The owner cut the migration on 2026-08-07 — existing pin state is wiped, not carried —
/// so the file's pin half is gone and seeding from it would resurrect a second source of truth
/// this ticket exists to remove.
/// </para>
/// <para>
/// 🔴 The sprint rule is filtered to the bound CANONICAL principal (ADO #1106, Spec #1103) — the
/// authenticated connection's <c>uniqueName</c>, resolved asynchronously through
/// <see cref="IIterationService.GetAuthenticatedUserIdentityAsync(System.Threading.CancellationToken)"/>.
/// A display rendering is deliberately NOT used, because two accounts can share one and a
/// display-keyed self filter would silently merge them. A connection that cannot supply a
/// canonical identity refuses the self default rather than widening to the whole team or falling
/// back to a display label; <c>--all</c> consumers never reach this path.
/// </para>
/// </summary>
public sealed class DefaultBenchSelectors
{
    /// <summary>
    /// Shared refusal when the authenticated connection carries no canonical identity. Surfaces
    /// catch and format the same text so a user sees a single actionable error whether the gap
    /// surfaces through <see cref="BuildAsync(System.Threading.CancellationToken)"/> defensively
    /// or through a pre-flight check on the surface.
    /// </summary>
    public const string MissingBoundIdentityMessage =
        "Default self-view requires a bound canonical identity, and the authenticated " +
        "connection did not return one. Enroll a bound principal with `twig connection` " +
        "or pass `--all` to request the explicit team view.";

    private readonly IIterationService _iterationService;

    public DefaultBenchSelectors(IIterationService iterationService)
    {
        _iterationService = iterationService;
    }

    /// <summary>Composes the selectors a freshly created default Bench holds.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the authenticated connection does not supply a canonical <c>uniqueName</c>.
    /// A read that falls through here without a bound identity has already lost — widening to
    /// display or the whole team is exactly the failure #1106 refuses.
    /// </exception>
    public async Task<IReadOnlyCollection<BenchSelector>> BuildAsync(CancellationToken ct = default)
    {
        var (_, uniqueName) = await _iterationService.GetAuthenticatedUserIdentityAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(uniqueName))
            throw new BoundIdentityUnavailableException();

        return new List<BenchSelector>
        {
            BenchSelector.ForCurrentSprintCanonical(uniqueName),
        };
    }
}

/// <summary>A self-derived consumer cannot proceed without the admitted account's canonical identity.</summary>
internal sealed class BoundIdentityUnavailableException()
    : InvalidOperationException(DefaultBenchSelectors.MissingBoundIdentityMessage);
