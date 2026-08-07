using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Reads the current Bench's pins as pins, for the sync path (ADO #146).
/// <para>
/// 🔴 The Bench is now the ONE source of truth for pins. Before this, the sync path read the
/// tracking FILE while pinning wrote to both — a dual-write kept only so #146 could be a data
/// move. The owner cut the migration (2026-08-07: existing pin state is wiped, not carried), so
/// the file half is deleted outright and this is what replaces it as the sync path's reader.
/// </para>
/// <para>
/// A subtree selector becomes a <see cref="TrackingMode.Tree"/> pin and an item selector a
/// <see cref="TrackingMode.Single"/> one, which is exactly the distinction the sync path acts on:
/// it refreshes the whole subtree under a tree pin and only the item under a single pin.
/// </para>
/// </summary>
public sealed class BenchPinReader : IPinReader, IPinWriter
{
    private readonly IBenchRepository _benchRepository;
    private readonly CurrentBenchResolver _currentBench;

    public BenchPinReader(IBenchRepository benchRepository, CurrentBenchResolver currentBench)
    {
        _benchRepository = benchRepository;
        _currentBench = currentBench;
    }

    public async Task AddPinAsync(int workItemId, TrackingMode mode, CancellationToken ct = default)
    {
        var bench = await _currentBench.ResolveAsync(ct);
        var selector = mode == TrackingMode.Tree
            ? BenchSelector.ForSubtree(workItemId)
            : BenchSelector.ForItem(workItemId);

        await _benchRepository.AddSelectorAsync(bench.Id, selector, ct);
    }

    public async Task<bool> RemovePinAsync(int workItemId, CancellationToken ct = default)
    {
        var bench = await _currentBench.ResolveAsync(ct);

        var item = BenchSelector.ForItem(workItemId);
        var subtree = BenchSelector.ForSubtree(workItemId);
        var was = bench.Selectors.Contains(item) || bench.Selectors.Contains(subtree);

        await _benchRepository.RemoveSelectorAsync(bench.Id, item, ct);
        await _benchRepository.RemoveSelectorAsync(bench.Id, subtree, ct);

        return was;
    }

    public async Task<IReadOnlyList<TrackedItem>> GetPinsAsync(CancellationToken ct = default)
    {
        var bench = await _currentBench.ResolveAsync(ct);

        var pins = new List<TrackedItem>();
        foreach (var selector in bench.Selectors)
        {
            // Query selectors are not pins. A pin is something the person placed by hand; a query
            // is a rule about a body of work, and refreshing "everything the sprint rule matches"
            // as though each were a hand pin would turn one sync into hundreds.
            var mode = selector.Kind switch
            {
                SelectorKind.Item => TrackingMode.Single,
                SelectorKind.Subtree => TrackingMode.Tree,
                _ => (TrackingMode?)null,
            };

            if (mode is null)
                continue;

            // TrackedAt is not stored on a selector and nothing in the sync path reads it — the
            // ordering it once carried was a display concern that moved to the Bench listing.
            pins.Add(new TrackedItem(selector.AsWorkItemId(), mode.Value, default));
        }

        return pins;
    }
}
