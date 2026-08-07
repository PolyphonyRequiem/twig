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
/// Consequence, stated plainly rather than discovered: <b>pins made before this ships do not
/// survive it.</b> That is the accepted cost of the wipe, not an oversight. Pins made after it
/// live on the Bench in the durable store, which is never dropped.
/// </para>
/// </summary>
public sealed class DefaultBenchSelectors
{
    private readonly string? _userDisplayName;

    /// <param name="userDisplayName">Who the sprint rule is filtered to, or null for the whole team.</param>
    public DefaultBenchSelectors(string? userDisplayName)
    {
        _userDisplayName = userDisplayName;
    }

    /// <summary>Composes the selectors a freshly created default Bench holds.</summary>
    public async Task<IReadOnlyCollection<BenchSelector>> BuildAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;

        return new List<BenchSelector>
        {
            BenchSelector.ForCurrentSprint(_userDisplayName),
        };
    }
}
