using System.Reflection;
using Shouldly;
using Twig.Domain.Services.Sync;
using Xunit;

namespace Twig.Domain.Tests.Architecture;

/// <summary>
/// The enforcement point the April 2026 orchestrator audit never had (issue #318).
/// </summary>
/// <remarks>
/// <para>
/// Finding 6 of <c>docs/architecture/domain-model-critique.md</c> audited five orchestrators and
/// set retention criteria for any future one: <i>"substantial logic, clean 1:1 command delegation,
/// and no overlap with existing services."</i> Nothing checked. A sixth
/// (<c>SeedDiscardOrchestrator</c>) was added the day AFTER the audit was written, was never
/// assessed against those criteria, and the critique went on advertising "✅ completed April 2026"
/// for three months.
/// </para>
/// <para>
/// The gap was never the sixth orchestrator itself — at ~125 lines with one consumer it would very
/// likely have passed. The gap is that an audit which sets criteria and has no enforcement point
/// degrades into a stale snapshot within a day. This test is that enforcement point: adding,
/// removing, or renaming an orchestrator fails here until the inventory below is updated
/// deliberately, which is the moment to apply the criteria.
/// </para>
/// <para>
/// <b>This is an inventory guard, not a quality gate.</b> It cannot judge "substantial logic" or
/// "no overlap with existing services" — a human does that when the list changes. What it
/// guarantees is that the list cannot change SILENTLY. Do not weaken it by loosening the
/// comparison; add the new entry and record the ruling in the critique.
/// </para>
/// </remarks>
public sealed class OrchestratorInventoryTests
{
    /// <summary>
    /// Every orchestrator in the domain, with the assessment that justifies its existence.
    /// Line counts are indicative and deliberately NOT asserted — they churn on every edit and a
    /// test that fails for a one-line change trains people to update it without thinking.
    /// </summary>
    private static readonly HashSet<string> DeclaredOrchestrators =
    [
        // Audited April 2026, retained.
        "RefreshOrchestrator",        // full refresh lifecycle: WIQL fetch, conflicts, hydration.
        "SeedPublishOrchestrator",    // seed -> ADO publish, incl. the durable intent record (0015).

        // Added 2026-04-28, one day after the audit; assessed retrospectively under #318 and
        // retained: single consumer, no overlap with SeedPublishOrchestrator's write path.
        "SeedDiscardOrchestrator",
    ];

    private static IReadOnlyList<Type> DiscoverOrchestrators() =>
        typeof(RefreshOrchestrator).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Orchestrator", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The guard. A new orchestrator must be declared above — which is the prompt to rule on it
    /// against finding 6's criteria rather than letting it appear unexamined.
    /// </summary>
    [Fact]
    public void EveryOrchestratorIsDeclared()
    {
        var discovered = DiscoverOrchestrators().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var undeclared = discovered.Except(DeclaredOrchestrators).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var stale = DeclaredOrchestrators.Except(discovered).OrderBy(n => n, StringComparer.Ordinal).ToList();

        undeclared.ShouldBeEmpty(
            "a new orchestrator appeared without being assessed against critique finding 6's " +
            "retention criteria (substantial logic, clean 1:1 command delegation, no overlap with " +
            "existing services). Add it to DeclaredOrchestrators with a one-line justification, " +
            "and record the ruling in docs/architecture/domain-model-critique.md");

        stale.ShouldBeEmpty(
            "a declared orchestrator no longer exists — remove it from DeclaredOrchestrators so " +
            "the inventory does not rot into the stale snapshot this test exists to prevent");
    }

    /// <summary>
    /// Non-vacuity control. A discovery predicate that silently matched nothing would make the
    /// guard above pass forever; this pins that the sweep actually finds the domain's
    /// orchestrators, so an empty result is a failure rather than a green run.
    /// </summary>
    [Fact]
    public void TheDiscoverySweepActuallyFindsOrchestrators()
    {
        var discovered = DiscoverOrchestrators();

        discovered.ShouldNotBeEmpty(
            "the type sweep found nothing — the guard above would pass against an empty set");
        discovered.Select(t => t.Name).ShouldContain(nameof(RefreshOrchestrator));
    }
}
