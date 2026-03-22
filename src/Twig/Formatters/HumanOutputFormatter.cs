using System.Text;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Formatters;

/// <summary>
/// Human-readable formatter with ANSI colors, box-drawing tree characters,
/// active/dirty markers, and numbered disambiguation.
/// </summary>
public sealed class HumanOutputFormatter : IOutputFormatter
{
    // ANSI escape sequences
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Red = "\x1b[31m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Blue = "\x1b[34m";
    private const string Magenta = "\x1b[35m";
    private const string Cyan = "\x1b[36m";
    private const string Dim = "\x1b[2m";

    private readonly Dictionary<string, string>? _typeColors;
    private readonly Dictionary<string, string>? _appearanceColors;
    private readonly Dictionary<string, string>? _typeIconIds;
    private readonly string _iconMode;
    private readonly IReadOnlyList<StateEntry>? _stateEntries;

    public HumanOutputFormatter() : this(new DisplayConfig()) { }

    public HumanOutputFormatter(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null, IReadOnlyList<StateEntry>? stateEntries = null)
    {
        _typeColors = displayConfig.TypeColors is null
            ? null
            : new Dictionary<string, string>(displayConfig.TypeColors, StringComparer.OrdinalIgnoreCase);
        _appearanceColors = typeAppearances?
            .Where(a => !string.IsNullOrEmpty(a.Color))
            .ToDictionary(a => a.Name, a => a.Color, StringComparer.OrdinalIgnoreCase);
        _iconMode = displayConfig.Icons;
        _typeIconIds = typeAppearances?
            .Where(a => a.IconId is not null)
            .ToDictionary(a => a.Name, a => a.IconId!, StringComparer.OrdinalIgnoreCase);
        _stateEntries = stateEntries;
    }

    public string FormatStatusSummary(WorkItem item)
    {
        var typeColor = GetTypeColor(item.Type);
        var badge = GetTypeBadge(item.Type);
        var stateColor = GetStateColor(item.State);
        return $"#{item.Id} {Cyan}●{Reset} {typeColor}{badge} {item.Type}{Reset} — {item.Title} [{stateColor}{item.State}{Reset}]";
    }

    public string FormatWorkItem(WorkItem item, bool showDirty)
    {
        var sb = new StringBuilder();
        var stateColor = GetStateColor(item.State);
        var dirty = showDirty && item.IsDirty ? $" {Yellow}•{Reset}" : "";

        sb.AppendLine($"{Bold}#{item.Id} {item.Title}{Reset}{dirty}");
        var typeColor = GetTypeColor(item.Type);
        var badge = GetTypeBadge(item.Type);
        sb.AppendLine($"  Type:      {typeColor}{badge} {item.Type}{Reset}");
        sb.AppendLine($"  State:     {stateColor}{item.State}{Reset}");
        sb.AppendLine($"  Assigned:  {item.AssignedTo ?? "(unassigned)"}");
        sb.AppendLine($"  Area:      {item.AreaPath}");
        sb.Append($"  Iteration: {item.IterationPath}");

        return sb.ToString();
    }

    public string FormatTree(WorkTree tree, int maxChildren, int? activeId)
    {
        var sb = new StringBuilder();
        var focusDepth = tree.ParentChain.Count;
        var lines = new List<AlignedLine>();

        // Parent chain — colorized badge, dimmed title
        for (var i = 0; i < tree.ParentChain.Count; i++)
        {
            var parent = tree.ParentChain[i];
            var indent = new string(' ', i * 2);
            var parentTypeColor = GetTypeColor(parent.Type);
            var parentStateColor = GetStateColor(parent.State);
            var badge = GetTypeBadge(parent.Type);
            lines.Add(new AlignedLine(
                $"{indent}{parentTypeColor}{badge}{Reset} {Dim}{parent.Title}{Reset}",
                $"[{parentStateColor}{parent.State}{Reset}]", ""));
        }

        // Focused item with active marker at its natural depth
        var focusIndent = new string(' ', focusDepth * 2);
        var focusDirty = tree.FocusedItem.IsDirty ? $" {Yellow}•{Reset}" : "";
        var focusStateColor = GetStateColor(tree.FocusedItem.State);
        var focusTypeColor = GetTypeColor(tree.FocusedItem.Type);
        var focusBadge = GetTypeBadge(tree.FocusedItem.Type);
        lines.Add(new AlignedLine(
            $"{focusIndent}{Cyan}●{Reset} {focusTypeColor}{focusBadge}{Reset} {Bold}#{tree.FocusedItem.Id} {tree.FocusedItem.Title}{Reset}",
            $"[{focusStateColor}{tree.FocusedItem.State}{Reset}]", focusDirty));

        // Children with box-drawing at focused+1 depth
        var childIndent = new string(' ', (focusDepth + 1) * 2);
        var displayCount = Math.Min(tree.Children.Count, maxChildren);
        var hasMore = tree.Children.Count > maxChildren;
        for (var i = 0; i < displayCount; i++)
        {
            var child = tree.Children[i];
            var isLast = i == displayCount - 1 && !hasMore;
            var connector = isLast ? "└── " : "├── ";
            var dirty = child.IsDirty ? $" {Yellow}•{Reset}" : "";
            var childStateColor = GetStateColor(child.State);
            var childTypeColor = GetTypeColor(child.Type);
            var childBadge = GetTypeBadge(child.Type);
            var activeMarker = (activeId.HasValue && child.Id == activeId.Value) ? $"{Cyan}●{Reset} " : "";
            lines.Add(new AlignedLine(
                $"{childIndent}{connector}{activeMarker}{childTypeColor}{childBadge}{Reset} #{child.Id} {child.Title}",
                $"[{childStateColor}{child.State}{Reset}]", dirty));
        }

        FlushAlignedLines(sb, lines);

        if (hasMore)
            sb.AppendLine($"{childIndent}└── {Dim}... and {tree.Children.Count - maxChildren} more{Reset}");

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatWorkspace(Workspace ws, int staleDays)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Bold}Workspace{Reset}");
        sb.AppendLine(new string('─', 50));

        // Active context
        if (ws.ContextItem is not null)
        {
            var dirty = ws.ContextItem.IsDirty ? $" {Yellow}•{Reset}" : "";
            sb.AppendLine($"  {Bold}Active:{Reset} #{ws.ContextItem.Id} {ws.ContextItem.Title}{dirty}");
            var stateColor = GetStateColor(ws.ContextItem.State);
            var typeColor = GetTypeColor(ws.ContextItem.Type);
            var badge = GetTypeBadge(ws.ContextItem.Type);
            sb.AppendLine($"          {typeColor}{badge} {ws.ContextItem.Type}{Reset} · {stateColor}{ws.ContextItem.State}{Reset}");
        }
        else
        {
            sb.AppendLine($"  {Bold}Active:{Reset} {Dim}(none){Reset}");
        }

        sb.AppendLine();

        // Sprint items grouped by state category
        sb.AppendLine($"  {Bold}Sprint ({ws.SprintItems.Count} items):{Reset}");
        var wsLines = new List<AlignedLine>();
        var categoryGroups = GroupByStateCategory(ws.SprintItems);
        foreach (var (category, items) in categoryGroups)
        {
            sb.AppendLine($"    {Bold}{FormatCategoryHeader(category)}{Reset} ({items.Count})");
            wsLines.Clear();
            if (ws.Hierarchy is not null)
            {
                // Render hierarchically — personal view has a single assignee, skip the assignee header
                foreach (var kvp in ws.Hierarchy.AssigneeGroups)
                {
                    foreach (var root in kvp.Value)
                    {
                        if (NodeOrDescendantBelongsToCategory(root, category))
                        {
                            CollectHierarchyNodeLine(wsLines, ws, root, indent: "      ", connector: "");
                            CollectHierarchyChildrenForCategory(wsLines, ws, root, childIndent: "      ", category: category);
                        }
                    }
                }
            }
            else
            {
                foreach (var item in items)
                {
                    var marker = (ws.ContextItem is not null && item.Id == ws.ContextItem.Id) ? $"{Cyan}●{Reset}" : " ";
                    var dirty = item.IsDirty ? $" {Yellow}•{Reset}" : "";
                    var stateColor = GetStateColor(item.State);
                    var sprintTypeColor = GetTypeColor(item.Type);
                    var sprintBadge = GetTypeBadge(item.Type);
                    wsLines.Add(new AlignedLine(
                        $"      {marker} {sprintTypeColor}{sprintBadge}{Reset} #{item.Id} {item.Title}",
                        $"[{stateColor}{item.State}{Reset}]", dirty));
                }
            }
            FlushAlignedLines(sb, wsLines);
        }

        // Seeds
        if (ws.Seeds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Bold}Seeds ({ws.Seeds.Count}):{Reset}");
            var staleSeeds = ws.GetStaleSeeds(staleDays);
            var staleSeedIds = new HashSet<int>(staleSeeds.Count);
            foreach (var s in staleSeeds)
                staleSeedIds.Add(s.Id);
            foreach (var seed in ws.Seeds)
            {
                var staleWarning = staleSeedIds.Contains(seed.Id) ? $" {Red}⚠ stale{Reset}" : "";
                var seedTypeColor = GetTypeColor(seed.Type);
                var seedBadge = GetTypeBadge(seed.Type);
                sb.AppendLine($"    {seedTypeColor}{seedBadge}{Reset} #{seed.Id} {seed.Title} ({seed.Type}){staleWarning}");
            }
        }

        // Dirty summary
        var dirtyItems = ws.GetDirtyItems();
        if (dirtyItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Yellow}{dirtyItems.Count} item(s) with unsaved changes.{Reset}");
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatSprintView(Workspace ws, int staleDays)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Bold}Sprint{Reset}");
        sb.AppendLine(new string('─', 50));

        // Active context
        if (ws.ContextItem is not null)
        {
            var dirty = ws.ContextItem.IsDirty ? $" {Yellow}•{Reset}" : "";
            sb.AppendLine($"  {Bold}Active:{Reset} #{ws.ContextItem.Id} {ws.ContextItem.Title}{dirty}");
        }

        sb.AppendLine();

        sb.AppendLine($"  {Bold}Sprint ({ws.SprintItems.Count} items):{Reset}");

        // Group by state category first, then by assignee within each category
        var categoryGroups = GroupByStateCategory(ws.SprintItems);
        foreach (var (category, categoryItems) in categoryGroups)
        {
            sb.AppendLine($"    {Bold}{FormatCategoryHeader(category)}{Reset} ({categoryItems.Count})");

            if (ws.Hierarchy is not null)
                RenderHierarchicalSprintForCategory(sb, ws, category);
            else
                RenderFlatSprintForCategory(sb, ws, categoryItems);
        }

        // Seeds
        if (ws.Seeds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Bold}Seeds ({ws.Seeds.Count}):{Reset}");
            var staleSeeds = ws.GetStaleSeeds(staleDays);
            var staleSeedIds = new HashSet<int>(staleSeeds.Count);
            foreach (var s in staleSeeds)
                staleSeedIds.Add(s.Id);
            foreach (var seed in ws.Seeds)
            {
                var staleWarning = staleSeedIds.Contains(seed.Id) ? $" {Red}⚠ stale{Reset}" : "";
                var seedTypeColor = GetTypeColor(seed.Type);
                var seedBadge = GetTypeBadge(seed.Type);
                sb.AppendLine($"    {seedTypeColor}{seedBadge}{Reset} #{seed.Id} {seed.Title} ({seed.Type}){staleWarning}");
            }
        }

        // Dirty summary
        var dirtyItems = ws.GetDirtyItems();
        if (dirtyItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Yellow}{dirtyItems.Count} item(s) with unsaved changes.{Reset}");
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    private void RenderFlatSprintForCategory(StringBuilder sb, Workspace ws, IReadOnlyList<WorkItem> categoryItems)
    {
        // Group items within this category by assignee
        var grouped = new Dictionary<string, List<WorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in categoryItems)
        {
            var assignee = item.AssignedTo ?? "(unassigned)";
            if (!grouped.TryGetValue(assignee, out var list))
            {
                list = new List<WorkItem>();
                grouped[assignee] = list;
            }
            list.Add(item);
        }

        var lines = new List<AlignedLine>();
        foreach (var kvp in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"      {Bold}{kvp.Key}{Reset} ({kvp.Value.Count}):");
            lines.Clear();
            foreach (var item in kvp.Value)
            {
                var marker = (ws.ContextItem is not null && item.Id == ws.ContextItem.Id) ? $"{Cyan}●{Reset}" : " ";
                var dirty = item.IsDirty ? $" {Yellow}•{Reset}" : "";
                var stateColor = GetStateColor(item.State);
                var sprintTypeColor = GetTypeColor(item.Type);
                var sprintBadge = GetTypeBadge(item.Type);
                var assignedSuffix = $" {Dim}@{item.AssignedTo ?? "(unassigned)"}{Reset}";
                lines.Add(new AlignedLine(
                    $"        {marker} {sprintTypeColor}{sprintBadge}{Reset} #{item.Id} {item.Title}{assignedSuffix}",
                    $"[{stateColor}{item.State}{Reset}]", dirty));
            }
            FlushAlignedLines(sb, lines);
        }
    }

    private void RenderHierarchicalSprintForCategory(StringBuilder sb, Workspace ws, StateCategory category)
    {
        var hierarchy = ws.Hierarchy!;
        var lines = new List<AlignedLine>();

        foreach (var kvp in hierarchy.AssigneeGroups)
        {
            // Only render nodes belonging to this category
            var hasNodesInCategory = false;
            foreach (var root in kvp.Value)
            {
                if (NodeOrDescendantBelongsToCategory(root, category))
                {
                    hasNodesInCategory = true;
                    break;
                }
            }
            if (!hasNodesInCategory) continue;

            // Count only sprint items in this category for the assignee header
            var sprintCount = 0;
            foreach (var root in kvp.Value)
                sprintCount += CountSprintItemsInCategory(root, category);
            if (sprintCount == 0) continue;

            sb.AppendLine($"      {Bold}{kvp.Key}{Reset} ({sprintCount}):");

            lines.Clear();
            foreach (var root in kvp.Value)
            {
                if (NodeOrDescendantBelongsToCategory(root, category))
                {
                    CollectHierarchyNodeLine(lines, ws, root, indent: "        ", connector: "", showAssignee: true);
                    CollectHierarchyChildrenForCategory(lines, ws, root, childIndent: "        ", category: category, showAssignee: true);
                }
            }
            FlushAlignedLines(sb, lines);
        }
    }

    private void CollectHierarchyNodeLine(List<AlignedLine> lines, Workspace ws, SprintHierarchyNode node, string indent, string connector, bool showAssignee = false)
    {
        var typeColor = GetTypeColor(node.Item.Type);
        var badge = GetTypeBadge(node.Item.Type);
        var stateColor = GetStateColor(node.Item.State);
        var progress = FormatProgressIndicator(node);

        if (node.IsSprintItem)
        {
            var marker = (ws.ContextItem is not null && node.Item.Id == ws.ContextItem.Id) ? $"{Cyan}●{Reset} " : "";
            var dirty = node.Item.IsDirty ? $" {Yellow}•{Reset}" : "";
            var assigneeSuffix = showAssignee ? $" {Dim}@{node.Item.AssignedTo ?? "(unassigned)"}{Reset}" : "";
            lines.Add(new AlignedLine(
                $"{indent}{connector}{marker}{typeColor}{badge}{Reset} #{node.Item.Id} {node.Item.Title}{progress}{assigneeSuffix}",
                $"[{stateColor}{node.Item.State}{Reset}]", dirty));
        }
        else
        {
            // Parent context node — dimmed title, no active/dirty markers
            var assigneeSuffix = showAssignee ? $" {Dim}@{node.Item.AssignedTo ?? "(unassigned)"}{Reset}" : "";
            lines.Add(new AlignedLine(
                $"{indent}{connector}{typeColor}{badge}{Reset} {Dim}{node.Item.Title}{Reset}{progress}{assigneeSuffix}",
                $"[{stateColor}{node.Item.State}{Reset}]", ""));
        }
    }

    private void CollectHierarchyChildren(List<AlignedLine> lines, Workspace ws, SprintHierarchyNode node, string childIndent, bool showAssignee = false)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var isLast = i == node.Children.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            var continuation = isLast ? "    " : "│   ";
            CollectHierarchyNodeLine(lines, ws, node.Children[i], childIndent, connector, showAssignee);
            CollectHierarchyChildren(lines, ws, node.Children[i], childIndent + continuation, showAssignee);
        }
    }

    private void CollectHierarchyChildrenForCategory(List<AlignedLine> lines, Workspace ws, SprintHierarchyNode node, string childIndent, StateCategory category, bool showAssignee = false)
    {
        // Pre-pass: find the last visible child index for correct box-drawing connectors.
        // Without this, a filtered sibling list produces a dangling "├──" on the last visible child.
        var lastVisibleIdx = -1;
        for (var j = 0; j < node.Children.Count; j++)
        {
            if (NodeOrDescendantBelongsToCategory(node.Children[j], category))
                lastVisibleIdx = j;
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (!NodeOrDescendantBelongsToCategory(child, category))
                continue;
            var isLast = i == lastVisibleIdx;
            var connector = isLast ? "└── " : "├── ";
            var continuation = isLast ? "    " : "│   ";
            CollectHierarchyNodeLine(lines, ws, child, childIndent, connector, showAssignee);
            CollectHierarchyChildrenForCategory(lines, ws, child, childIndent + continuation, category: category, showAssignee: showAssignee);
        }
    }

    public string FormatFieldChange(FieldChange change)
    {
        return $"  {Bold}{change.FieldName}{Reset}: {Dim}{change.OldValue ?? "(empty)"}{Reset} → {change.NewValue ?? "(empty)"}";
    }

    // ── State category grouping ─────────────────────────────────────

    /// <summary>
    /// Groups work items by state category in display order (Proposed → InProgress → Resolved → Completed).
    /// Categories with no items are omitted. Removed and Unknown are omitted from display.
    /// </summary>
    internal IReadOnlyList<(StateCategory Category, IReadOnlyList<WorkItem> Items)> GroupByStateCategory(IReadOnlyList<WorkItem> items)
    {
        var groups = new Dictionary<StateCategory, List<WorkItem>>
        {
            [StateCategory.Proposed] = new(),
            [StateCategory.InProgress] = new(),
            [StateCategory.Resolved] = new(),
            [StateCategory.Completed] = new(),
        };

        foreach (var item in items)
        {
            var category = StateCategoryResolver.Resolve(item.State, _stateEntries);
            if (groups.TryGetValue(category, out var list))
                list.Add(item);
            else
                groups[StateCategory.Proposed].Add(item); // Unknown/Removed → Proposed bucket
        }

        var result = new List<(StateCategory, IReadOnlyList<WorkItem>)>();
        StateCategory[] displayOrder = [StateCategory.Proposed, StateCategory.InProgress, StateCategory.Resolved, StateCategory.Completed];
        foreach (var cat in displayOrder)
        {
            if (groups[cat].Count > 0)
                result.Add((cat, groups[cat]));
        }
        return result;
    }

    internal static string FormatCategoryHeader(StateCategory category)
    {
        return category switch
        {
            StateCategory.Proposed => "Proposed",
            StateCategory.InProgress => "In Progress",
            StateCategory.Resolved => "Resolved",
            StateCategory.Completed => "Completed",
            _ => category.ToString(),
        };
    }

    private bool NodeOrDescendantBelongsToCategory(SprintHierarchyNode node, StateCategory category)
    {
        if (node.IsSprintItem && StateCategoryResolver.Resolve(node.Item.State, _stateEntries) == category)
            return true;
        foreach (var child in node.Children)
        {
            if (NodeOrDescendantBelongsToCategory(child, category))
                return true;
        }
        return false;
    }

    private int CountSprintItemsInCategory(SprintHierarchyNode node, StateCategory category)
    {
        var count = 0;
        if (node.IsSprintItem && StateCategoryResolver.Resolve(node.Item.State, _stateEntries) == category)
            count++;
        foreach (var child in node.Children)
            count += CountSprintItemsInCategory(child, category);
        return count;
    }

    // ── Progress indicators ─────────────────────────────────────────

    /// <summary>
    /// Formats a progress indicator for parent items: [done/total] where done = resolved + completed children.
    /// Returns empty string if the node has no children.
    /// </summary>
    internal string FormatProgressIndicator(SprintHierarchyNode node)
    {
        if (node.Children.Count == 0) return "";

        var total = 0;
        var done = 0;
        CountChildProgress(node, ref total, ref done);
        return $" {Dim}[{done}/{total}]{Reset}";
    }

    private void CountChildProgress(SprintHierarchyNode node, ref int total, ref int done)
    {
        foreach (var child in node.Children)
        {
            if (child.IsSprintItem)
            {
                total++;
                var category = StateCategoryResolver.Resolve(child.Item.State, _stateEntries);
                if (category == StateCategory.Resolved || category == StateCategory.Completed)
                    done++;
            }
            // Recurse into grandchildren
            CountChildProgress(child, ref total, ref done);
        }
    }

    public string FormatError(string message)
    {
        return $"{Red}error:{Reset} {message}";
    }

    public string FormatSuccess(string message)
    {
        return $"{Green}✓{Reset} {message}";
    }

    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Multiple matches:");
        for (var i = 0; i < matches.Count; i++)
        {
            var (id, title) = matches[i];
            sb.AppendLine($"  {Bold}[{i + 1}]{Reset} #{id} {title}");
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatHint(string hint)
    {
        return $"{Dim}  hint: {hint}{Reset}";
    }

    public string FormatInfo(string message)
    {
        return $"{Dim}{message}{Reset}";
    }

    public string FormatBranchInfo(string branchName)
    {
        return $"  Branch:    {Cyan}{branchName}{Reset}";
    }

    public string FormatPrStatus(int prId, string title, string status)
    {
        var statusColor = status switch
        {
            "active" => Blue,
            "completed" => Green,
            "abandoned" => Red,
            _ => Dim,
        };
        return $"  PR !{prId}: {title} [{statusColor}{status}{Reset}]";
    }

    public string FormatAnnotatedLogEntry(string hash, string message, string? workItemType, string? workItemState, int? workItemId)
    {
        var shortHash = hash.Length > 7 ? hash[..7] : hash;
        if (workItemType is not null && workItemId.HasValue)
        {
            var typeResult = Domain.ValueObjects.WorkItemType.Parse(workItemType);
            if (typeResult.IsSuccess)
            {
                var badge = GetTypeBadge(typeResult.Value);
                var typeColor = GetTypeColor(typeResult.Value);
                var stateStr = workItemState is not null ? $" [{workItemState}]" : "";
                return $"{Yellow}{shortHash}{Reset} {message} {typeColor}{badge}{Reset} #{workItemId}{stateStr}";
            }
        }

        return $"{Yellow}{shortHash}{Reset} {message}";
    }

    private string GetTypeColor(WorkItemType type)
    {
        var hex = TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors);
        if (hex is not null)
        {
            var ansi = HexToAnsi.ToForeground(hex);
            if (ansi is not null)
                return ansi;
        }

        return DeterministicTypeColor.GetAnsiEscape(type.Value);
    }

    private string GetTypeBadge(WorkItemType type)
    {
        return IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds);
    }

    private string GetStateColor(string state)
    {
        if (string.IsNullOrEmpty(state))
            return Dim;

        return StateCategoryResolver.Resolve(state, _stateEntries) switch
        {
            StateCategory.Completed or StateCategory.Resolved => Green,
            StateCategory.InProgress => Blue,
            StateCategory.Removed => Red,
            StateCategory.Proposed => Dim,
            _ => Reset,
        };
    }

    // ── Alignment helpers ───────────────────────────────────────────

    private readonly record struct AlignedLine(string Prefix, string State, string Suffix);

    private static int VisibleLength(string s)
    {
        var len = 0;
        var inEscape = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\x1b') { inEscape = true; continue; }
            if (inEscape) { if (c == 'm') inEscape = false; continue; }

            // Surrogate pair — decode the full codepoint
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, s[i + 1]);
                i++; // skip the low surrogate
                // Supplementary Private Use Area-A (nerd font nf-md-* icons): double-width
                len += (cp >= 0xF0000) ? 2 : 1;
            }
            else
            {
                // BMP characters including PUA — single terminal column each
                len++;
            }
        }
        return len;
    }

    private static void FlushAlignedLines(StringBuilder sb, List<AlignedLine> lines)
    {
        if (lines.Count == 0) return;

        // Right-align states to terminal width (leave room for state + suffix)
        var termWidth = GetTerminalWidth();

        // Find the longest state tag so we know how much space to reserve
        var maxStateVisible = 0;
        foreach (var line in lines)
        {
            var sl = VisibleLength(line.State) + VisibleLength(line.Suffix);
            if (sl > maxStateVisible) maxStateVisible = sl;
        }

        // The alignment column is terminal width minus the widest state+suffix, minus 1 for spacing
        var alignColumn = termWidth - maxStateVisible - 1;

        // Clamp so we don't push states into the prefix
        var maxPrefixVisible = 0;
        foreach (var line in lines)
        {
            var vl = VisibleLength(line.Prefix);
            if (vl > maxPrefixVisible) maxPrefixVisible = vl;
        }

        // Alignment column must be at least as wide as the longest prefix + 1
        if (alignColumn < maxPrefixVisible + 1)
            alignColumn = maxPrefixVisible + 1;

        foreach (var line in lines)
        {
            var pad = alignColumn - VisibleLength(line.Prefix);
            if (pad < 1) pad = 1;
            sb.Append(line.Prefix);
            sb.Append(' ', pad);
            sb.Append(line.State);
            sb.AppendLine(line.Suffix);
        }

        lines.Clear();
    }

    private static int GetTerminalWidth()
    {
        try { return Console.WindowWidth; }
        catch (Exception) { return 120; }
    }

}
