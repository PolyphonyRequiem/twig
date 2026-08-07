using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Pins and unpins an item on the CURRENT Bench (ADO #145, docs/specs/bench.spec.md §2).
/// <para>
/// 🔴 A pin is not a different kind of thing from a query — it is a selector that matches one
/// item, and a tree pin is a selector that matches an item and its descendants AS THEY ARE NOW.
/// The subtree is never expanded here into a set of ids: expansion happens at evaluation time in
/// <see cref="BenchEvaluator"/>, which is what makes a subtree selector match a child created
/// after the pin. Capturing ids at pin time passes every other test and gets that one wrong.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// This is the existing MUTATION-WORKFLOW seam: one workflow per operation, returning a result
/// type, with both the CLI and the agent surface routing through it. The adapters resolve the
/// target and render the outcome and decide nothing about what a pin means, so the two surfaces
/// cannot drift — the defect that made every agent-surface tool name its own target.
/// </para>
/// <para>
/// 🔴 THE BENCH IS THE ONLY PIN STORE (ADO #146). The dual write to the tracking file that stood
/// here until 2026-08-07 is gone, along with the file's pin half. The owner cut the migration —
/// existing pin state is wiped rather than carried — so there is nothing to reconcile and no
/// second store to drift from.
/// </para>
/// <para>
/// What made the dual write necessary has moved rather than disappeared: tracked-tree refresh,
/// the cleanup policy and the agent surface's tracking status used to READ the file. They now read
/// the Bench through <c>IPinReader</c>. That pairing is the point — a reader and a writer on the
/// same store cannot disagree, and the failure they used to risk was invisible to the parity
/// baseline, which covers the computed view and not the sync path.
/// </para>
/// </remarks>
public sealed class PinWorkflow(
    IBenchRepository benchRepository,
    DefaultBenchSelectors defaultSelectors,
    CurrentBenchResolver? currentBench = null)
{
    /// <summary>
    /// Adds a pin to the current Bench. Idempotent: pinning twice leaves one selector, so the
    /// membership of a Bench cannot be changed by repetition.
    /// </summary>
    /// <param name="workItemId">The item to pin.</param>
    /// <param name="includeSubtree">
    /// True for a tree pin — a subtree selector, which keeps matching descendants added later.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PinOutcome> PinAsync(
        int workItemId, bool includeSubtree, CancellationToken ct = default)
    {
        var bench = await CurrentBenchAsync(ct);

        var selector = includeSubtree
            ? BenchSelector.ForSubtree(workItemId)
            : BenchSelector.ForItem(workItemId);

        await benchRepository.AddSelectorAsync(bench.Id, selector, ct);

        return new PinOutcome.Pinned(await CurrentBenchAsync(ct), workItemId, includeSubtree);
    }

    /// <summary>
    /// Removes every pin on the current Bench that names <paramref name="workItemId"/> — both the
    /// item selector and the subtree selector, since the person asked to stop following that item
    /// and does not know which kind they created.
    /// <para>
    /// Reports <see cref="PinOutcome.Unpinned.WasPinned"/> as false when nothing was there rather
    /// than failing: unpinning something never pinned is not a fault the person can act on.
    /// </para>
    /// </summary>
    public async Task<PinOutcome> UnpinAsync(int workItemId, CancellationToken ct = default)
    {
        var bench = await CurrentBenchAsync(ct);

        var item = BenchSelector.ForItem(workItemId);
        var subtree = BenchSelector.ForSubtree(workItemId);

        var wasOnTheBench = bench.Selectors.Contains(item) || bench.Selectors.Contains(subtree);

        await benchRepository.RemoveSelectorAsync(bench.Id, item, ct);
        await benchRepository.RemoveSelectorAsync(bench.Id, subtree, ct);

        return new PinOutcome.Unpinned(await CurrentBenchAsync(ct), workItemId, wasOnTheBench);
    }

    /// <summary>
    /// The Bench a pin acts on: the one the person is standing on (#149), which is the default
    /// until they switch.
    /// <para>
    /// 🔴 Resolution is delegated to <see cref="CurrentBenchResolver"/> rather than re-derived
    /// here. A second copy of "read the pointer, else the default" is how a pin ends up landing on
    /// the default while the person is looking at another Bench — the pin simply does not appear,
    /// with nothing failing to say why.
    /// </para>
    /// <para>
    /// The default is created on first use with the same selectors the view would have created it
    /// with, so whether the person's first command after upgrading is a read or a pin, the default
    /// Bench comes out the same.
    /// </para>
    /// </summary>
    private async Task<Bench> CurrentBenchAsync(CancellationToken ct)
    {
        var resolver = currentBench ?? new CurrentBenchResolver(benchRepository, defaultSelectors);
        return await resolver.ResolveAsync(ct);
    }
}
