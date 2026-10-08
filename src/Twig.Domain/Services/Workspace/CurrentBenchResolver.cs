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
/// on every membership resolve (ADO #1106). A default persisted before the canonical slot existed carries a
/// display-rendered <see cref="BenchSelector.ForCurrentSprint(string?)"/>; evaluating that today
/// could merge two accounts with the same display name, or route a different operator's view
/// through the previous user's display name. Normalizing in-memory keeps pins intact and never
/// overwrites CUSTOM Benches whose rules are the user's own authored choices.
/// Metadata-only consumers use <see cref="ResolveStoredAsync"/> instead: they report durable
/// arrangements, never evaluate their self rules, and leave stored selectors unchanged.
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
    public Task<Bench> ResolveAsync(CancellationToken ct = default, string? expectBench = null)
        => ResolveCoreAsync(normalizeDefaultSelf: true, ct, expectBench);

    /// <summary>
    /// Resolves the same current pointer and default policy for metadata-only consumers, returning
    /// durable selectors without rebinding or evaluating the default's self rule. An existing
    /// default needs no identity discovery; first-use creation still requires canonical identity.
    /// Membership and query consumers must use <see cref="ResolveAsync"/> instead.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="expectBench">Optional captured current Bench ID, checked before first-use initialization.</param>
    public Task<Bench> ResolveStoredAsync(CancellationToken ct = default, string? expectBench = null)
        => ResolveCoreAsync(normalizeDefaultSelf: false, ct, expectBench);

    private async Task<Bench> ResolveCoreAsync(bool normalizeDefaultSelf, CancellationToken ct, string? expectBench)
    {
        var current = await _benchRepository.GetCurrentAsync(ct)
            ?? await _benchRepository.GetByNameAsync(Bench.DefaultName, ct);
        if (expectBench is not null)
        {
            // A stale browser must not even create the first-use default Bench.
            if (current is null || !string.Equals(expectBench,
                current.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The current Bench changed (expected '{expectBench}', current '{current?.Id.ToString(CultureInfo.InvariantCulture) ?? "none"}'). Refresh the browser and retry; no pins were changed.");
        }
        if (current is null)
        {
            var freshSelectors = await _defaultSelectors.BuildAsync(ct);
            current = await _benchRepository.GetOrCreateDefaultAsync(freshSelectors, ct);
            return normalizeDefaultSelf ? NormalizeDefaultSelfSprint(current, freshSelectors) : current;
        }

        if (normalizeDefaultSelf && current.IsDefault)
            return NormalizeDefaultSelfSprint(current, await _defaultSelectors.BuildAsync(ct));

        return current;
    }

    internal async Task<Bench> ReadStoredAsync(Bench captured, CancellationToken ct = default)
    {
        var stored = await _benchRepository.GetByNameAsync(captured.Name, ct);
        if (stored is null || stored.Id != captured.Id)
            throw new InvalidOperationException("The captured Bench is no longer available. Refresh and retry.");
        return stored;
    }

    internal async Task<(Bench Effective, Bench Stored)> ResolveCapturedAsync(CancellationToken ct = default, string? expectBench = null)
        => await ReadCapturedAsync(await ResolveStoredAsync(ct, expectBench), ct);

    internal async Task<(Bench Effective, Bench Stored)> ReadCapturedAsync(Bench captured, CancellationToken ct = default)
    {
        var stored = await ReadStoredAsync(captured, ct);
        return (stored.IsDefault ? NormalizeDefaultSelfSprint(stored, await _defaultSelectors.BuildAsync(ct)) : stored, stored);
    }

    /// <summary>
    /// Replaces the default Bench's sprint-rule query selectors with the current bound-principal
    /// canonical form, preserving every non-sprint selector exactly. Pins (item / subtree) and any
    /// unrelated query rule survive — only the automanaged sprint rule is re-sourced.
    /// </summary>
    private static Bench NormalizeDefaultSelfSprint(Bench current, IReadOnlyCollection<BenchSelector> freshSelectors)
    {
        var uniqueName = freshSelectors.Single(s => s.Kind == SelectorKind.Query).QueryAssignedToUniqueName!;
        var combined = new List<BenchSelector>(current.Selectors.Count);
        foreach (var selector in current.Selectors)
        {
            if (!IsAutomanagedSprintRule(selector))
                combined.Add(selector);
            else if (selector.QueryRule == BenchSelector.CurrentSprintRule)
                combined.Add((BenchQueryRule.Parse(selector) with { AssignedTo = null, UniqueName = uniqueName })
                    .ToLegacySelector());
            else
                combined.Add((BenchQueryRule.Parse(selector) with { AssignedTo = null, UniqueName = uniqueName }).ToSelector());
        }
        return current with { Selectors = combined };
    }

    private static bool IsAutomanagedSprintRule(BenchSelector selector)
        => selector.Kind == SelectorKind.Query
            && selector.QueryRule is BenchSelector.CurrentSprintRule or BenchQueryRule.Name;
}
