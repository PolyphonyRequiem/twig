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
/// Pins are still read from the FILE here because migrating them into the durable store is ticket
/// #146, deliberately not #145. Until that lands the file and the Bench coexist; because both are
/// already expressed as selectors, #146 is a data move rather than a second rewrite of this logic.
/// </para>
/// </summary>
public sealed class DefaultBenchSelectors
{
    private readonly ITrackingRepository? _trackingRepo;
    private readonly string? _userDisplayName;

    /// <param name="trackingRepo">
    /// The tracking file, or null when no tracking is wired up (the Bench then starts with the
    /// sprint rule alone).
    /// </param>
    /// <param name="userDisplayName">Who the sprint rule is filtered to, or null for the whole team.</param>
    public DefaultBenchSelectors(ITrackingRepository? trackingRepo, string? userDisplayName)
    {
        _trackingRepo = trackingRepo;
        _userDisplayName = userDisplayName;
    }

    /// <summary>Composes the selectors a freshly created default Bench holds.</summary>
    public async Task<IReadOnlyCollection<BenchSelector>> BuildAsync(CancellationToken ct = default)
    {
        var selectors = new List<BenchSelector>
        {
            BenchSelector.ForCurrentSprint(_userDisplayName),
        };

        if (_trackingRepo is not null)
        {
            foreach (var tracked in await _trackingRepo.GetAllTrackedAsync(ct))
            {
                selectors.Add(tracked.Mode == Enums.TrackingMode.Tree
                    ? BenchSelector.ForSubtree(tracked.WorkItemId)
                    : BenchSelector.ForItem(tracked.WorkItemId));
            }
        }

        return selectors;
    }
}
