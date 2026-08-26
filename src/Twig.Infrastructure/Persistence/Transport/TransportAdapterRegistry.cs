using System.Collections.Generic;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §7.2 <c>ITransportAdapterRegistry</c>. String-equality
/// lookup by <see cref="ITransportAdapter.AdapterId"/>. No fallback,
/// no discovery, no ordering-driven priority. Unknown <c>adapterId</c>
/// raises <see cref="TransportAttachmentFailure.AdapterNotRegistered"/>.
/// <para>
/// The registry is populated by a singleton factory-lambda in
/// <see cref="TwigServiceRegistration"/> matching the existing
/// <c>AddConnectionServices</c> pattern. Adapters are injected by
/// constructor list; nothing is reflective; the composition is
/// source-generated-friendly and AOT-compatible.
/// </para>
/// </summary>
internal interface ITransportAdapterRegistry
{
    /// <summary>Resolve the adapter for a given
    /// <see cref="TransportAdapterTarget.AdapterId"/>. §7.3 explicitly
    /// forbids a silent fallback to the null adapter for an
    /// unregistered <c>adapterId</c> — that would be
    /// authorization-neutrality laundering.</summary>
    Result<ITransportAdapter> Resolve(string adapterId);

    /// <summary>Snapshot of every registered adapter. Ordering is
    /// deterministic by <see cref="ITransportAdapter.AdapterId"/> so the
    /// diagnostics surface is stable across process restarts.</summary>
    IReadOnlyList<ITransportAdapter> All { get; }
}

/// <summary>
/// Concrete registry: a plain immutable dictionary keyed by
/// <see cref="ITransportAdapter.AdapterId"/> string equality. Duplicate
/// registration throws on construction (bug rail); no runtime hot-swap
/// (§7.5 defers hot-swap).
/// </summary>
internal sealed class TransportAdapterRegistry : ITransportAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, ITransportAdapter> _byId;
    private readonly IReadOnlyList<ITransportAdapter> _all;

    public TransportAdapterRegistry(IEnumerable<ITransportAdapter> adapters)
    {
        var byId = new Dictionary<string, ITransportAdapter>(System.StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (string.IsNullOrEmpty(adapter.AdapterId))
                throw new System.ArgumentException("Adapter has an empty AdapterId; registration is refused.", nameof(adapters));
            if (byId.ContainsKey(adapter.AdapterId))
                throw new System.ArgumentException($"Duplicate adapter registration for AdapterId '{adapter.AdapterId}'.", nameof(adapters));
            byId[adapter.AdapterId] = adapter;
        }
        _byId = byId;
        // Deterministic ordering by AdapterId.
        var ordered = new List<ITransportAdapter>(byId.Values);
        ordered.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.AdapterId, b.AdapterId));
        _all = ordered;
    }

    public Result<ITransportAdapter> Resolve(string adapterId)
    {
        if (string.IsNullOrEmpty(adapterId))
            return Result.Fail<ITransportAdapter>(TransportAttachmentFailure.AdapterNotRegistered);
        if (!_byId.TryGetValue(adapterId, out var adapter))
            return Result.Fail<ITransportAdapter>(TransportAttachmentFailure.AdapterNotRegistered);
        return Result.Ok(adapter);
    }

    public IReadOnlyList<ITransportAdapter> All => _all;
}
