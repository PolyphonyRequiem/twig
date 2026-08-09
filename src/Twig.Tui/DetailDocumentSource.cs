using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;

namespace Twig.Tui;

/// <summary>
/// Produces the <see cref="WorkItemDetailDocument"/> the TUI paints: acquires the
/// server-authored layout for an item's type, and falls back to
/// <see cref="FallbackFormLayout"/> when the server serves none.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is the ONLY place in the TUI that decides which fields a form has</b>, and it
/// decides it by choosing a <see cref="FormLayout"/> — never by listing fields. Both
/// branches end in the same <see cref="WorkItemDetailProjector.Project"/> call, so the view
/// downstream cannot tell them apart and has nothing to special-case.
/// </para>
/// <para>
/// The two absent-layout cases are distinct and stay distinct: a provider returning
/// <c>null</c> means <i>no layout was served</i> and routes to the fallback; a served layout
/// with no pages means <i>the server says there are no controls</i> and is projected as-is,
/// producing an empty form. Collapsing them would make an empty server form silently sprout
/// Twig-authored rows.
/// </para>
/// <para>
/// Layouts are cached per work item type for the session. The provider hits ADO REST, and
/// the tree fires a selection on every keypress.
/// </para>
/// </remarks>
internal sealed class DetailDocumentSource
{
    private readonly IFormLayoutProvider? _layoutProvider;
    private readonly WorkItemMapper _mapper;
    private readonly Dictionary<string, FormLayout?> _layoutCache = new(StringComparer.OrdinalIgnoreCase);

    internal DetailDocumentSource(IFormLayoutProvider? layoutProvider, WorkItemMapper? mapper = null)
    {
        _layoutProvider = layoutProvider;
        _mapper = mapper ?? new WorkItemMapper();
    }

    /// <summary>
    /// Gets the detail document for <paramref name="item"/>.
    /// </summary>
    internal async Task<WorkItemDetailDocument> GetAsync(WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var snapshot = _mapper.ToSnapshot(item);
        var layout = await GetLayoutAsync(item.Type.ToString(), ct).ConfigureAwait(false);

        return WorkItemDetailProjector.Project(layout ?? FallbackFormLayout.For(snapshot), snapshot);
    }

    private async Task<FormLayout?> GetLayoutAsync(string typeName, CancellationToken ct)
    {
        if (_layoutProvider is null) return null;
        if (_layoutCache.TryGetValue(typeName, out var cached)) return cached;

        FormLayout? layout;
        try
        {
            layout = await _layoutProvider.GetFormLayoutAsync(typeName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreachable or erroring server is the same situation as one that serves no
            // layout: Twig does not know the form's structure. Degrade to the fallback rather
            // than blank the pane.
            layout = null;
        }

        _layoutCache[typeName] = layout;
        return layout;
    }
}
