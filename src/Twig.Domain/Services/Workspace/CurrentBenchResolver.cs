using System.Globalization;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Answers "which Bench am I standing on?" — once, for every caller (ADO #149,
/// docs/specs/bench.spec.md §5).
/// <para>
/// 🔴 This exists so there is exactly ONE resolution of the current Bench. Before switching, three
/// call sites each independently asked for the default; if each of them had instead grown its own
/// "read the pointer, else the default", one of them would eventually have been left reading the
/// default after a switch, and the symptom would be a view quietly showing the wrong arrangement
/// with nothing to fail.
/// </para>
/// <para>
/// A stored pointer that no longer resolves — the Bench was deleted — falls back to the default
/// rather than throwing. That is NOT the unknown-Bench error in disguise: the unknown-Bench error
/// is about a name a PERSON just typed, which must fail loudly because they are wrong about the
/// world. A dangling stored pointer is twig's own bookkeeping, the person named nothing, and there
/// is no wrong target to act on because the default cannot go missing.
/// </para>
/// <para>
/// 🔴 The DEFAULT Bench's self sprint selector is re-bound to the current authenticated principal
/// on every resolve (ADO #1106). A default persisted before the canonical slot existed carries a
/// display-rendered <see cref="BenchSelector.ForCurrentSprint(string?)"/>; evaluating that today
/// could merge two accounts with the same display name, or route a different operator's view
/// through the previous user's display name. Normalizing in-memory keeps pins intact and never
/// overwrites CUSTOM Benches whose rules are the user's own authored choices.
/// </para>
/// </summary>
public sealed class CurrentBenchResolver
{
    private readonly IBenchRepository _benchRepository;
    private readonly DefaultBenchSelectors _defaultSelectors;

    public CurrentBenchResolver(IBenchRepository benchRepository, DefaultBenchSelectors defaultSelectors)
    {
        _benchRepository = benchRepository;
        _defaultSelectors = defaultSelectors;
    }

    /// <summary>
    /// The Bench commands act on: the one last switched to, or the default when nobody has
    /// switched.
    /// <para>
    /// The default is created here if it does not exist yet, with the same selectors the view
    /// would have created it with, so whether the person's first command after upgrading is a
    /// read, a pin or a listing, the default Bench comes out the same.
    /// </para>
    /// <para>
    /// When the current Bench IS the default, its sprint rule is re-sourced from the bound
    /// principal before returning — a Bench persisted before the canonical slot existed is
    /// transparently normalized without discarding its pins. A custom Bench is returned
    /// untouched, because its selectors are the user's own rules.
    /// </para>
    /// </summary>
    public async Task<Bench> ResolveAsync(CancellationToken ct = default, string? expectBench = null)
    {
        var current = await _benchRepository.GetCurrentAsync(ct);
        if (expectBench is not null)
        {
            // A stale browser must not even create the first-use default Bench.
            current ??= await _benchRepository.GetByNameAsync(Bench.DefaultName, ct);
            if (current is null || !string.Equals(expectBench,
                current.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The current Bench changed (expected '{expectBench}', current '{current?.Id.ToString(CultureInfo.InvariantCulture) ?? "none"}'). Refresh the browser and retry; no pins were changed.");
        }
        if (current is null)
        {
            var freshSelectors = await _defaultSelectors.BuildAsync(ct);
            current = await _benchRepository.GetOrCreateDefaultAsync(freshSelectors, ct);
            return NormalizeDefaultSelfSprint(current, freshSelectors);
        }

        if (current.IsDefault)
            return NormalizeDefaultSelfSprint(current, await _defaultSelectors.BuildAsync(ct));

        return current;
    }

    /// <summary>
    /// Replaces the default Bench's sprint-rule query selectors with the current bound-principal
    /// canonical form, preserving every non-sprint selector exactly. Pins (item / subtree) and any
    /// unrelated query rule survive — only the automanaged sprint rule is re-sourced.
    /// </summary>
    private static Bench NormalizeDefaultSelfSprint(Bench current, IReadOnlyCollection<BenchSelector> freshSelectors)
    {
        var combined = new List<BenchSelector>(freshSelectors.Count + current.Selectors.Count);
        combined.AddRange(freshSelectors);
        foreach (var selector in current.Selectors)
        {
            if (!IsAutomanagedSprintRule(selector))
                combined.Add(selector);
        }
        return current with { Selectors = combined };
    }

    private static bool IsAutomanagedSprintRule(BenchSelector selector)
        => selector.Kind == SelectorKind.Query
            && string.Equals(selector.QueryRule, BenchSelector.CurrentSprintRule, StringComparison.Ordinal);
}
