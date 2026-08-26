using System.Collections.Generic;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §10.2 <c>IChangeProposalPresentationSupportRegistry</c>.
/// Deterministic registry populated at DI composition time from a
/// compile-time list; nothing reflective, nothing runs I/O, and
/// <see cref="IsSupported"/> is pure.
/// <para>
/// AB#745 registers exactly one entry — the terminal/text fallback
/// support — as a baseline; downstream renderer builds add
/// rich-adapter entries when they implement them.
/// </para>
/// </summary>
internal interface IChangeProposalPresentationSupportRegistry
{
    /// <summary>§10.2 predicate — decides whether THIS renderer build
    /// knows how to invoke the rich adapter identified by
    /// <paramref name="id"/>. Distinct from a §3.3 capability
    /// declaration: an adapter declaring <c>StatusReporting</c> does
    /// not thereby become a supported rich-render target.</summary>
    bool IsSupported(RichAdapterId id);

    /// <summary>§10.2 diagnostics / conformance surface — returns
    /// every <see cref="RichAdapterId"/> the registry supports.
    /// </summary>
    IReadOnlyList<RichAdapterId> RegisteredRichAdapters { get; }
}

/// <summary>
/// Compile-time registry: constructor takes the fixed set of
/// supported <see cref="RichAdapterId"/> entries and answers by string
/// equality. The universal terminal/text fallback is NOT stored here —
/// it is the unconditional path handled by
/// <see cref="ChangeProposalRenderer"/>.
/// </summary>
internal sealed class ChangeProposalPresentationSupportRegistry : IChangeProposalPresentationSupportRegistry
{
    private readonly HashSet<RichAdapterId> _supported;
    private readonly IReadOnlyList<RichAdapterId> _all;

    public ChangeProposalPresentationSupportRegistry(IEnumerable<RichAdapterId> supported)
    {
        _supported = new HashSet<RichAdapterId>(supported);
        // Deterministic ordering by adapterId then role for stable
        // diagnostics.
        var list = new List<RichAdapterId>(_supported);
        list.Sort((a, b) =>
        {
            var cmp = System.StringComparer.Ordinal.Compare(a.AdapterId, b.AdapterId);
            return cmp != 0 ? cmp : ((int)a.Role).CompareTo((int)b.Role);
        });
        _all = list;
    }

    public bool IsSupported(RichAdapterId id) => _supported.Contains(id);
    public IReadOnlyList<RichAdapterId> RegisteredRichAdapters => _all;
}
