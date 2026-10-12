namespace Twig.Browser;

/// <summary>Pure local hierarchy index; folds and tree/table navigation never touch a repository.</summary>
internal sealed class BrowserBenchIndex
{
    private readonly IReadOnlyList<BrowserBenchItem> _items;
    private readonly Dictionary<int, BrowserBenchItem> _byId;
    private readonly Dictionary<int, BrowserBenchItem[]> _children;
    private readonly BrowserBenchItem[] _roots;
    private readonly BrowserBenchItem[] _members;

    internal BrowserBenchIndex(IReadOnlyList<BrowserBenchItem> items)
    {
        _items = items;
        _byId = _items.ToDictionary(i => i.Id);
        _children = _items.Where(i => i.ParentId.HasValue).GroupBy(i => i.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).ToArray());
        _roots = _items.Where(i => !i.ParentId.HasValue || !_byId.ContainsKey(i.ParentId.Value)).OrderBy(i => i.Id).ToArray();
        _members = _items.Where(i => i.Member).ToArray();
    }

    internal bool HasChildren(int id) => _children.ContainsKey(id);
    internal int Depth(int id, int width)
    {
        var seen = new HashSet<int>(); var depth = 0;
        if (!_byId.TryGetValue(id, out var current)) return 0;
        while (current.ParentId is { } parent && seen.Add(parent) && _byId.TryGetValue(parent, out current)) depth++;
        return Math.Min(depth, Math.Max(0, (width - 12) / 2));
    }

    internal IEnumerable<BrowserBenchItem> Visible(string view, IReadOnlySet<int> folded)
    {
        if (view == "table") { foreach (var item in _members) yield return item; yield break; }
        var visited = new HashSet<int>();
        var stack = new Stack<BrowserBenchItem>(_roots.Reverse());
        while (stack.TryPop(out var item))
        {
            if (!visited.Add(item.Id)) continue;
            yield return item;
            if (!folded.Contains(item.Id) && _children.TryGetValue(item.Id, out var nested))
                for (var i = nested.Length - 1; i >= 0; i--) stack.Push(nested[i]);
        }
        // Unreachable cached cycles remain selectable; a folded ancestor still hides descendants.
        foreach (var item in _items)
        {
            if (visited.Contains(item.Id)) continue;
            var current = item; var seen = new HashSet<int>(); var hidden = false;
            while (current.ParentId is { } parent && seen.Add(parent) && _byId.TryGetValue(parent, out var ancestor))
            { if (parent != item.Id && folded.Contains(parent)) { hidden = true; break; } current = ancestor; }
            if (!hidden) yield return item;
        }
    }
}
