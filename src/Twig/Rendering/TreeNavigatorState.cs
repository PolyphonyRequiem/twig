using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Rendering;

/// <summary>
/// A non-hierarchy link used for Tab-cycling in the interactive navigator.
/// Merges <see cref="WorkItemLink"/> and <see cref="SeedLink"/> into a uniform shape.
/// </summary>
public readonly record struct NavigatorLink(int TargetId, string LinkType, bool IsSeed);

/// <summary>
/// Mutable state for the interactive tree navigator. Tracks cursor position,
/// visible siblings, children, filter text, and link jump index.
/// Mutable by design (RD-007) — rebuilt on each node load, mutated on each keystroke.
/// </summary>
public sealed class TreeNavigatorState
{
    private IReadOnlyList<WorkItem> _allSiblings;
    private List<WorkItem> _visibleSiblings;
    private int _priorCursorIndex;

    public TreeNavigatorState(
        WorkItem? cursorItem,
        IReadOnlyList<WorkItem> parentChain,
        IReadOnlyList<WorkItem> allSiblings,
        IReadOnlyList<WorkItem> children,
        IReadOnlyList<WorkItemLink> links,
        IReadOnlyList<SeedLink> seedLinks)
    {
        CursorItem = cursorItem;
        ParentChain = parentChain;
        _allSiblings = allSiblings;
        _visibleSiblings = new List<WorkItem>(allSiblings);
        Children = children;
        Links = links;
        SeedLinks = seedLinks;

        // Set initial cursor index to the position of cursorItem in siblings
        CursorIndex = cursorItem is not null
            ? Math.Max(0, _visibleSiblings.FindIndex(w => w.Id == cursorItem.Id))
            : 0;
    }

    /// <summary>Currently highlighted item.</summary>
    public WorkItem? CursorItem { get; private set; }

    /// <summary>Root → cursor parent (ancestors, rendered dimmed in tree).</summary>
    public IReadOnlyList<WorkItem> ParentChain { get; private set; }

    /// <summary>Children of cursor's parent, used for ↑/↓ navigation.</summary>
    public IReadOnlyList<WorkItem> VisibleSiblings => _visibleSiblings;

    /// <summary>Cursor item's own children, shown below cursor.</summary>
    public IReadOnlyList<WorkItem> Children { get; private set; }

    /// <summary>Cursor position within <see cref="VisibleSiblings"/>.</summary>
    public int CursorIndex { get; private set; }

    /// <summary>Current filter string.</summary>
    public string FilterText { get; private set; } = string.Empty;

    /// <summary>Whether filter mode is active.</summary>
    public bool IsFilterMode { get; private set; }

    /// <summary>Non-hierarchy links for the cursor item.</summary>
    public IReadOnlyList<WorkItemLink> Links { get; private set; }

    /// <summary>Seed links for the cursor item.</summary>
    public IReadOnlyList<SeedLink> SeedLinks { get; private set; }

    /// <summary>Current link cycle position for Tab traversal (-1 = no link selected).</summary>
    public int LinkJumpIndex { get; private set; } = -1;

    /// <summary>Decrement CursorIndex (floor 0), update CursorItem.</summary>
    public void MoveCursorUp()
    {
        if (_visibleSiblings.Count == 0) return;
        if (CursorIndex > 0)
            CursorIndex--;
        CursorItem = _visibleSiblings[CursorIndex];
    }

    /// <summary>Increment CursorIndex (cap at VisibleSiblings.Count-1), update CursorItem.</summary>
    public void MoveCursorDown()
    {
        if (_visibleSiblings.Count == 0) return;
        if (CursorIndex < _visibleSiblings.Count - 1)
            CursorIndex++;
        CursorItem = _visibleSiblings[CursorIndex];
    }

    /// <summary>Sets Children to the provided list.</summary>
    public void Expand(IReadOnlyList<WorkItem> children)
    {
        Children = children;
    }

    /// <summary>Clears Children.</summary>
    public void Collapse()
    {
        Children = Array.Empty<WorkItem>();
    }

    /// <summary>
    /// Sets FilterText, sets IsFilterMode=true, narrows VisibleSiblings to items
    /// whose Title contains text (case-insensitive), resets CursorIndex to 0.
    /// Saves the pre-filter cursor index so <see cref="ClearFilter"/> can restore it.
    /// </summary>
    public void ApplyFilter(string text)
    {
        if (!IsFilterMode)
            _priorCursorIndex = CursorIndex;
        FilterText = text;
        IsFilterMode = true;
        _visibleSiblings = _allSiblings
            .Where(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        CursorIndex = 0;
        CursorItem = _visibleSiblings.Count > 0 ? _visibleSiblings[0] : null;
    }

    /// <summary>
    /// Clears FilterText, sets IsFilterMode=false, restores VisibleSiblings from all siblings.
    /// Restores the cursor index to the pre-filter position (clamped to bounds).
    /// </summary>
    public void ClearFilter()
    {
        FilterText = string.Empty;
        IsFilterMode = false;
        _visibleSiblings = new List<WorkItem>(_allSiblings);
        CursorIndex = _visibleSiblings.Count > 0
            ? Math.Min(_priorCursorIndex, _visibleSiblings.Count - 1)
            : 0;
        CursorItem = _visibleSiblings.Count > 0 ? _visibleSiblings[CursorIndex] : null;
    }

    /// <summary>
    /// Returns merged list of Links (IsSeed=false) and SeedLinks (IsSeed=true).
    /// </summary>
    public IReadOnlyList<NavigatorLink> GetCombinedLinks()
    {
        var combined = new List<NavigatorLink>(Links.Count + SeedLinks.Count);
        for (var i = 0; i < Links.Count; i++)
        {
            var link = Links[i];
            combined.Add(new NavigatorLink(link.TargetId, link.LinkType, IsSeed: false));
        }
        for (var i = 0; i < SeedLinks.Count; i++)
        {
            var sl = SeedLinks[i];
            combined.Add(new NavigatorLink(sl.TargetId, sl.LinkType, IsSeed: true));
        }
        return combined;
    }

    /// <summary>
    /// Exits filter mode while keeping the cursor on the currently selected item.
    /// Restores all siblings and repositions cursor to match the selected item.
    /// </summary>
    public void AcceptFilter()
    {
        var selectedItem = CursorItem;
        FilterText = string.Empty;
        IsFilterMode = false;
        _visibleSiblings = new List<WorkItem>(_allSiblings);

        if (selectedItem is not null)
        {
            var idx = _visibleSiblings.FindIndex(w => w.Id == selectedItem.Id);
            CursorIndex = Math.Max(0, idx);
            CursorItem = _visibleSiblings.Count > 0 ? _visibleSiblings[CursorIndex] : null;
        }
        else
        {
            CursorIndex = 0;
            CursorItem = _visibleSiblings.Count > 0 ? _visibleSiblings[0] : null;
        }
    }

    /// <summary>
    /// Advances <see cref="LinkJumpIndex"/> forward through the combined link list (wrapping).
    /// Returns the <see cref="NavigatorLink"/> at the new index, or <c>null</c> if no links exist.
    /// </summary>
    public NavigatorLink? AdvanceLinkJump()
    {
        var combined = GetCombinedLinks();
        if (combined.Count == 0) return null;
        LinkJumpIndex = (LinkJumpIndex + 1) % combined.Count;
        return combined[LinkJumpIndex];
    }

    /// <summary>
    /// Moves <see cref="LinkJumpIndex"/> backward through the combined link list (wrapping).
    /// Returns the <see cref="NavigatorLink"/> at the new index, or <c>null</c> if no links exist.
    /// </summary>
    public NavigatorLink? ReverseLinkJump()
    {
        var combined = GetCombinedLinks();
        if (combined.Count == 0) return null;
        LinkJumpIndex = LinkJumpIndex <= 0 ? combined.Count - 1 : LinkJumpIndex - 1;
        return combined[LinkJumpIndex];
    }

    /// <summary>
    /// Updates children, links, seed links, and resets the link jump index.
    /// Called after loading node data for the current cursor item.
    /// </summary>
    internal void UpdateNodeData(
        IReadOnlyList<WorkItem> children,
        IReadOnlyList<WorkItemLink> links,
        IReadOnlyList<SeedLink> seedLinks)
    {
        Children = children;
        Links = links;
        SeedLinks = seedLinks;
        LinkJumpIndex = -1;
    }
}
