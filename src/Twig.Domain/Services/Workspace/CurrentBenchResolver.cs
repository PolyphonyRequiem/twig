using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

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
    /// </summary>
    public async Task<Bench> ResolveAsync(CancellationToken ct = default)
    {
        var current = await _benchRepository.GetCurrentAsync(ct);
        if (current is not null)
            return current;

        return await _benchRepository.GetOrCreateDefaultAsync(await _defaultSelectors.BuildAsync(ct), ct);
    }
}
