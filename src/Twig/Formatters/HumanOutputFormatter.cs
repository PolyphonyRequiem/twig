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
        return FormatWorkItem(item, showDirty, fieldDefinitions: null);
    }

    public string FormatWorkItem(WorkItem item, bool showDirty,
        IReadOnlyList<FieldDefinition>? fieldDefinitions,
        IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null,
        (int Done, int Total)? childProgress = null,
        (int FieldCount, int NoteCount)? pendingChanges = null,
        IReadOnlyList<WorkItemLink>? links = null)
    {
        var sb = new StringBuilder();
        var stateColor = GetStateColor(item.State);
        var dirty = showDirty && item.IsDirty ? $" {Yellow}✎{Reset}" : "";

        sb.AppendLine($"{Bold}#{item.Id} {item.Title}{Reset}{dirty}");
        var typeColor = GetTypeColor(item.Type);
        var badge = GetTypeBadge(item.Type);
        sb.AppendLine($"  Type:      {typeColor}{badge} {item.Type}{Reset}");
        sb.AppendLine($"  State:     {stateColor}{item.State}{Reset}");
        sb.AppendLine($"  Assigned:  {item.AssignedTo ?? "(unassigned)"}");
        sb.AppendLine($"  Area:      {item.AreaPath}");
        sb.Append($"  Iteration: {item.IterationPath}");

        // Extended fields section — append populated Fields with display names
        if (item.Fields.Count > 0)
        {
            var defLookup = BuildFieldDefinitionLookup(fieldDefinitions);
            var extendedFields = GetExtendedFields(item, defLookup, statusFieldEntries);
            if (extendedFields.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"  {Dim}── Extended ──────────────────{Reset}");
                foreach (var (displayName, value) in extendedFields)
                {
                    var label = displayName + ":";
                    var padding = Math.Max(1, 13 - label.Length);
                    sb.AppendLine($"  {label}{new string(' ', padding)}{value}");
                }
                // Remove trailing newline from last AppendLine
                if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
                    sb.Length -= 1;
                if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                    sb.Length -= 1;
            }
        }

        // EPIC-004 ITEM-018: Progress bar for parent items
        if (childProgress is { Total: > 0 } cp)
        {
            var progressBar = FormatterHelpers.BuildProgressBar(cp.Done, cp.Total);
            if (!string.IsNullOrEmpty(progressBar))
            {
                sb.AppendLine();
                sb.Append($"  Progress:  {progressBar}");
            }
        }

        // EPIC-004 ITEM-018: Consolidated pending changes footer (only non-zero segments)
        if (pendingChanges is { } pc && (pc.FieldCount > 0 || pc.NoteCount > 0))
        {
            sb.AppendLine();
            var parts = new List<string>();
            if (pc.FieldCount > 0)
            {
                var fieldLabel = pc.FieldCount == 1 ? "field change" : "field changes";
                parts.Add($"{pc.FieldCount} {fieldLabel}");
            }
            if (pc.NoteCount > 0)
            {
                var noteLabel = pc.NoteCount == 1 ? "note" : "notes";
                parts.Add($"{pc.NoteCount} {noteLabel} staged");
            }
            sb.Append($"  {Dim}{string.Join(", ", parts)}{Reset}");
        }

        // Links section — non-hierarchy links for the work item
        if (links is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"  {Dim}── Links ─────────────────────{Reset}");
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                sb.Append($"  {Blue}{link.LinkType}{Reset}: #{link.TargetId}");
                if (i < links.Count - 1)
                    sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public string FormatTree(WorkTree tree, int maxChildren, int? activeId)
    {
        return FormatTree(tree, maxChildren, activeId, typeLevelMap: null, parentChildMap: null);
    }

    public string FormatTree(WorkTree tree, int maxChildren, int? activeId,
        IReadOnlyDictionary<string, int>? typeLevelMap,
        IReadOnlyDictionary<string, List<string>>? parentChildMap)
    {
        var sb = new StringBuilder();

        // EPIC-005: Unparented banner for tree view
        if (typeLevelMap is not null && parentChildMap is not null
            && tree.ParentChain.Count == 0
            && !tree.FocusedItem.ParentId.HasValue
            && typeLevelMap.TryGetValue(tree.FocusedItem.Type.Value, out var focusLevel)
            && focusLevel > 0)
        {
            // Find expected parent type name from parentChildMap
            var expectedParent = FindExpectedParentTypeName(tree.FocusedItem.Type.Value, parentChildMap);
            var parentLabel = expectedParent ?? "a parent";
            sb.AppendLine($"{Dim}(unparented — expected under a {parentLabel}){Reset}");
        }

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

            // Sibling count indicator for parent chain nodes (skip root nodes with no parent)
            if (tree.SiblingCounts is not null && parent.ParentId.HasValue
                && tree.SiblingCounts.TryGetValue(parent.Id, out var parentSibCount) && parentSibCount.HasValue)
            {
                lines.Add(new AlignedLine($"{indent}{Dim}...{parentSibCount.Value}{Reset}", "", ""));
            }
        }

        // Focused item with active marker at its natural depth
        var focusIndent = new string(' ', focusDepth * 2);
        var focusDirty = tree.FocusedItem.IsDirty ? $" {Yellow}✎{Reset}" : "";
        var focusStateColor = GetStateColor(tree.FocusedItem.State);
        var focusTypeColor = GetTypeColor(tree.FocusedItem.Type);
        var focusBadge = GetTypeBadge(tree.FocusedItem.Type);
        lines.Add(new AlignedLine(
            $"{focusIndent}{Cyan}●{Reset} {focusTypeColor}{focusBadge}{Reset} {Bold}#{tree.FocusedItem.Id} {tree.FocusedItem.Title}{Reset}",
            $"[{focusStateColor}{tree.FocusedItem.State}{Reset}]", focusDirty));

        // Sibling count indicator for focused item (skip root nodes with no parent)
        if (tree.SiblingCounts is not null && tree.FocusedItem.ParentId.HasValue
            && tree.SiblingCounts.TryGetValue(tree.FocusedItem.Id, out var focusSibCount) && focusSibCount.HasValue)
        {
            lines.Add(new AlignedLine($"{focusIndent}{Dim}...{focusSibCount.Value}{Reset}", "", ""));
        }

        // Children with box-drawing at focused+1 depth
        var childIndent = new string(' ', (focusDepth + 1) * 2);
        var displayCount = Math.Min(tree.Children.Count, maxChildren);
        var hasMore = tree.Children.Count > maxChildren;
        for (var i = 0; i < displayCount; i++)
        {
            var child = tree.Children[i];
            var isLast = i == displayCount - 1 && !hasMore;
            var connector = isLast ? "└── " : "├── ";
            var dirty = child.IsDirty ? $" {Yellow}✎{Reset}" : "";
            var childStateColor = GetStateColor(child.State);
            var childTypeColor = GetTypeColor(child.Type);
            var childBadge = GetTypeBadge(child.Type);
            var activeMarker = (activeId.HasValue && child.Id == activeId.Value) ? $"{Cyan}●{Reset} " : "";
            var effort = FormatterHelpers.GetEffortDisplay(child);
            var effortSuffix = effort is not null ? $" {Dim}{effort}{Reset}" : "";
            lines.Add(new AlignedLine(
                $"{childIndent}{childStateColor}{connector}{Reset}{activeMarker}{childTypeColor}{childBadge}{Reset} #{child.Id} {child.Title}",
                $"[{childStateColor}{child.State}{Reset}]{effortSuffix}", dirty));
        }

        FlushAlignedLines(sb, lines);

        if (hasMore)
            sb.AppendLine($"{childIndent}└── {Dim}... and {tree.Children.Count - maxChildren} more{Reset}");

        // Links section — non-hierarchy links for the focused item
        if (tree.FocusedItemLinks.Count > 0)
        {
            var linkIndent = new string(' ', (focusDepth + 1) * 2);
            sb.AppendLine($"{linkIndent}{Dim}┊{Reset}");
            sb.AppendLine($"{linkIndent}╰── ⇄ Links");
            var linkLines = new List<AlignedLine>();
            for (var i = 0; i < tree.FocusedItemLinks.Count; i++)
            {
                var link = tree.FocusedItemLinks[i];
                var isLastLink = i == tree.FocusedItemLinks.Count - 1;
                var linkConnector = isLastLink ? "└── " : "├── ";
                linkLines.Add(new AlignedLine(
                    $"{linkIndent}    {linkConnector}{Blue}{link.LinkType}{Reset}: #{link.TargetId}",
                    "", ""));
            }
            FlushAlignedLines(sb, linkLines);
        }

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
            var dirty = ws.ContextItem.IsDirty ? $" {Yellow}✎{Reset}" : "";
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
        var wsCatIndex = 0;
        foreach (var (category, items) in categoryGroups)
        {
            // Insert separator between category groups (not before the first)
            if (wsCatIndex > 0)
                sb.AppendLine($"    {Dim}────{Reset}");

            sb.AppendLine($"    {Bold}{FormatCategoryHeader(category)}{Reset} ({items.Count})");
            wsLines.Clear();
            if (ws.Hierarchy is not null)
            {
                // Render hierarchically — personal view has a single assignee, skip the assignee header
                foreach (var kvp in ws.Hierarchy.AssigneeGroups)
                {
                    foreach (var root in kvp.Value)
                    {
                        if (root.IsVirtualGroup)
                        {
                            // Check if any child of the virtual group belongs to this category
                            var hasVisibleChild = false;
                            foreach (var child in root.Children)
                            {
                                if (NodeOrDescendantBelongsToCategory(child, category))
                                {
                                    hasVisibleChild = true;
                                    break;
                                }
                            }
                            if (hasVisibleChild)
                            {
                                RenderVirtualGroupForCategory(sb, wsLines, ws, root, category);
                            }
                        }
                        else if (NodeOrDescendantBelongsToCategory(root, category))
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
                    var dirty = item.IsDirty ? $" {Yellow}✎{Reset}" : "";
                    var stateColor = GetStateColor(item.State);
                    var sprintTypeColor = GetTypeColor(item.Type);
                    var sprintBadge = GetTypeBadge(item.Type);
                    wsLines.Add(new AlignedLine(
                        $"      {marker} {sprintTypeColor}{sprintBadge}{Reset} #{item.Id} {item.Title}",
                        $"[{stateColor}{item.State}{Reset}]", dirty));
                }
            }
            FlushAlignedLines(sb, wsLines);
            wsCatIndex++;
        }

        // Sprint progress summary
        if (ws.SprintItems.Count > 0)
        {
            var proposed = 0;
            var inProgressCount = 0;
            var doneCount = 0;
            foreach (var item in ws.SprintItems)
            {
                switch (StateCategoryResolver.Resolve(item.State, _stateEntries))
                {
                    case StateCategory.Proposed: proposed++; break;
                    case StateCategory.InProgress: inProgressCount++; break;
                    case StateCategory.Resolved:
                    case StateCategory.Completed: doneCount++; break;
                    case StateCategory.Removed: proposed++; break; // Removed bucketed with Proposed
                    case StateCategory.Unknown: proposed++; break; // Unknown bucketed with Proposed
                }
            }
            var total = ws.SprintItems.Count;
            var segments = new List<string>();
            segments.Add($"{Green}{doneCount}/{total}{Reset} done");
            if (inProgressCount > 0)
                segments.Add($"{Blue}{inProgressCount}{Reset} in progress");
            if (proposed > 0)
                segments.Add($"{Dim}{proposed}{Reset} proposed");
            sb.AppendLine();
            sb.AppendLine($"  Sprint: {string.Join(" · ", segments)}");
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
            var dirty = ws.ContextItem.IsDirty ? $" {Yellow}✎{Reset}" : "";
            sb.AppendLine($"  {Bold}Active:{Reset} #{ws.ContextItem.Id} {ws.ContextItem.Title}{dirty}");
        }

        sb.AppendLine();

        sb.AppendLine($"  {Bold}Sprint ({ws.SprintItems.Count} items):{Reset}");

        // Group by assignee at top level (no state category wrapper)
        if (ws.Hierarchy is not null)
            RenderHierarchicalSprint(sb, ws);
        else
            RenderFlatSprint(sb, ws);

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

    private void RenderFlatSprint(StringBuilder sb, Workspace ws)
    {
        // Group items by assignee
        var grouped = new Dictionary<string, List<WorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ws.SprintItems)
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
            sb.AppendLine($"    {Bold}{kvp.Key}{Reset} ({kvp.Value.Count}):");
            lines.Clear();
            foreach (var item in kvp.Value)
            {
                var marker = (ws.ContextItem is not null && item.Id == ws.ContextItem.Id) ? $"{Cyan}●{Reset}" : " ";
                var dirty = item.IsDirty ? $" {Yellow}✎{Reset}" : "";
                var stateColor = GetStateColor(item.State);
                var sprintTypeColor = GetTypeColor(item.Type);
                var sprintBadge = GetTypeBadge(item.Type);
                lines.Add(new AlignedLine(
                    $"      {marker} {sprintTypeColor}{sprintBadge}{Reset} #{item.Id} {item.Title}",
                    $"[{stateColor}{item.State}{Reset}]", dirty));
            }
            FlushAlignedLines(sb, lines);
        }
    }

    private void RenderHierarchicalSprint(StringBuilder sb, Workspace ws)
    {
        var hierarchy = ws.Hierarchy!;
        var lines = new List<AlignedLine>();

        foreach (var kvp in hierarchy.AssigneeGroups)
        {
            // Count sprint items for the assignee header
            var sprintCount = 0;
            foreach (var root in kvp.Value)
                sprintCount += CountSprintItems(root);
            if (sprintCount == 0) continue;

            sb.AppendLine($"    {Bold}{kvp.Key}{Reset} ({sprintCount}):");

            lines.Clear();
            foreach (var root in kvp.Value)
            {
                if (root.IsVirtualGroup)
                {
                    RenderVirtualGroupLine(sb, lines, ws, root);
                }
                else
                {
                    CollectHierarchyNodeLine(lines, ws, root, indent: "      ", connector: "", showAssignee: false);
                    CollectHierarchyChildren(lines, ws, root, childIndent: "      ", showAssignee: false);
                }
            }
            FlushAlignedLines(sb, lines);
        }
    }

    private static int CountSprintItems(SprintHierarchyNode node)
    {
        if (node.IsVirtualGroup)
        {
            var count = 0;
            foreach (var child in node.Children)
                count += CountSprintItems(child);
            return count;
        }
        var c = node.IsSprintItem ? 1 : 0;
        foreach (var child in node.Children)
            c += CountSprintItems(child);
        return c;
    }

    /// <summary>
    /// Renders a virtual group header (e.g., "── Unparented Tasks ──") and its children
    /// with backlog-level-aware indentation.
    /// </summary>
    private void RenderVirtualGroupLine(StringBuilder sb, List<AlignedLine> lines, Workspace ws, SprintHierarchyNode virtualNode)
    {
        // Flush any accumulated lines before the separator
        FlushAlignedLines(sb, lines);

        // Virtual group header at base indent (separator label)
        var baseIndent = "      ";
        sb.AppendLine($"{baseIndent}{Dim}── {virtualNode.GroupLabel} ──{Reset}");

        // Children rendered at their backlog-level indent WITHOUT connectors,
        // so badges align vertically with parented items at the same level.
        // (Level 0 badge at 6, level 1 at 10, level 2 at 14 — matching normal roots.)
        var itemIndent = baseIndent + new string(' ', virtualNode.BacklogLevel * 4);

        foreach (var child in virtualNode.Children)
        {
            CollectHierarchyNodeLine(lines, ws, child, itemIndent, connector: "", showAssignee: false);
            CollectHierarchyChildren(lines, ws, child, itemIndent, showAssignee: false);
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
            var dirty = node.Item.IsDirty ? $" {Yellow}✎{Reset}" : "";
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

    /// <summary>
    /// Renders a virtual group header and its children filtered by state category for workspace view.
    /// </summary>
    private void RenderVirtualGroupForCategory(StringBuilder sb, List<AlignedLine> lines, Workspace ws, SprintHierarchyNode virtualNode, StateCategory category)
    {
        FlushAlignedLines(sb, lines);

        var baseIndent = "      ";
        sb.AppendLine($"{baseIndent}{Dim}── {virtualNode.GroupLabel} ──{Reset}");

        var levelIndent = new string(' ', virtualNode.BacklogLevel * 4);
        var childIndent = baseIndent + levelIndent;

        // Pre-pass: find the last visible child
        var lastVisibleIdx = -1;
        for (var j = 0; j < virtualNode.Children.Count; j++)
        {
            if (NodeOrDescendantBelongsToCategory(virtualNode.Children[j], category))
                lastVisibleIdx = j;
        }

        for (var i = 0; i < virtualNode.Children.Count; i++)
        {
            var child = virtualNode.Children[i];
            if (!NodeOrDescendantBelongsToCategory(child, category))
                continue;
            var isLast = i == lastVisibleIdx;
            var connector = isLast ? "└── " : "├── ";
            var continuation = isLast ? "    " : "│   ";
            CollectHierarchyNodeLine(lines, ws, child, childIndent, connector);
            CollectHierarchyChildrenForCategory(lines, ws, child, childIndent + continuation, category: category);
        }
    }

    public string FormatFieldChange(FieldChange change)
    {
        return $"  {Bold}{change.FieldName}{Reset}: {Dim}{change.OldValue ?? "(empty)"}{Reset} {Green}→{Reset} {change.NewValue ?? "(empty)"}";
    }

    // ── State category grouping ─────────────────────────────────────

    /// <summary>
    /// Finds the expected parent type name for a given child type using the parent-child map.
    /// Returns null if no parent type is found.
    /// </summary>
    internal static string? FindExpectedParentTypeName(string childTypeName, IReadOnlyDictionary<string, List<string>> parentChildMap)
    {
        foreach (var (parentType, children) in parentChildMap)
        {
            foreach (var child in children)
            {
                if (string.Equals(child, childTypeName, StringComparison.OrdinalIgnoreCase))
                    return parentType;
            }
        }
        return null;
    }

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
        return $"{Red}✗ error:{Reset} {message}";
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

    /// <summary>
    /// Enriched disambiguation with type badge and state color when available.
    /// </summary>
    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title, string? TypeName, string? State)> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Multiple matches:");
        for (var i = 0; i < matches.Count; i++)
        {
            var (id, title, typeName, state) = matches[i];
            var badgePrefix = "";
            if (typeName is not null)
            {
                var parseResult = WorkItemType.Parse(typeName);
                if (parseResult.IsSuccess)
                {
                    var typeColor = GetTypeColor(parseResult.Value);
                    var badge = GetTypeBadge(parseResult.Value);
                    badgePrefix = $"{typeColor}{badge}{Reset} ";
                }
            }
            var stateSuffix = "";
            if (state is not null)
            {
                var stateColor = GetStateColor(state);
                stateSuffix = $" [{stateColor}{state}{Reset}]";
            }
            sb.AppendLine($"  {Bold}[{i + 1}]{Reset} {badgePrefix}#{id} {title}{stateSuffix}");
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
        return $"{Yellow}→{Reset} {Dim}hint: {hint}{Reset}";
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

    public string FormatSeedView(
        IReadOnlyList<SeedViewGroup> groups,
        int totalWritableFields,
        int staleDays,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null)
    {
        var sb = new StringBuilder();
        var totalSeeds = 0;
        foreach (var g in groups)
            totalSeeds += g.Seeds.Count;

        sb.AppendLine($"{Bold}Seeds ({totalSeeds}){Reset}");
        sb.AppendLine(new string('─', 50));

        if (totalSeeds == 0)
        {
            sb.Append($"  {Dim}No seeds{Reset}");
            return sb.ToString();
        }

        foreach (var group in groups)
        {
            sb.AppendLine();
            if (group.Parent is not null)
            {
                var parentTypeColor = GetTypeColor(group.Parent.Type);
                var parentBadge = GetTypeBadge(group.Parent.Type);
                sb.AppendLine($"  {Bold}Parent:{Reset} #{group.Parent.Id} {parentTypeColor}{parentBadge} {group.Parent.Type}{Reset} — {group.Parent.Title}");
            }
            else
            {
                sb.AppendLine($"  {Bold}Orphan Seeds{Reset}");
            }

            foreach (var seed in group.Seeds)
            {
                var seedTypeColor = GetTypeColor(seed.Type);
                var seedBadge = GetTypeBadge(seed.Type);
                var age = FormatSeedAge(seed.SeedCreatedAt);
                var filled = CountNonEmptyFields(seed);
                var staleWarning = IsStaleSeed(seed, staleDays) ? $" {Red}⚠ stale{Reset}" : "";
                sb.AppendLine($"    #{seed.Id}  {seedTypeColor}{seedBadge} {seed.Type}{Reset}  {seed.Title}  {Dim}{age}{Reset}  {Dim}{filled}/{totalWritableFields} fields{Reset}{staleWarning}");

                // Display links for this seed
                if (links is not null && links.TryGetValue(seed.Id, out var seedLinks))
                {
                    foreach (var link in seedLinks)
                    {
                        var annotation = FormatLinkAnnotation(seed.Id, link);
                        sb.AppendLine($"      {Cyan}→ {annotation}{Reset}");
                    }
                }
            }
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatSeedLinks(IReadOnlyList<SeedLink> links)
    {
        if (links.Count == 0)
            return $"{Dim}No virtual links.{Reset}";

        var sb = new StringBuilder();
        sb.AppendLine($"{Bold}Virtual Links ({links.Count}){Reset}");
        sb.AppendLine(new string('─', 50));

        foreach (var link in links)
        {
            sb.AppendLine($"  #{link.SourceId} {Cyan}──{link.LinkType}──▶{Reset} #{link.TargetId}  {Dim}{link.CreatedAt:yyyy-MM-dd}{Reset}");
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatSeedValidation(IReadOnlyList<SeedValidationResult> results)
    {
        if (results.Count == 0)
            return $"{Dim}No seeds to validate.{Reset}";

        var sb = new StringBuilder();
        var passCount = results.Count(r => r.Passed);

        sb.AppendLine($"{Bold}Seed Validation ({passCount}/{results.Count} passed){Reset}");
        sb.AppendLine(new string('─', 50));

        foreach (var result in results)
        {
            if (result.Passed)
            {
                sb.AppendLine($"  {Green}✔{Reset} #{result.SeedId}  {result.Title}");
            }
            else
            {
                sb.AppendLine($"  {Red}✘{Reset} #{result.SeedId}  {result.Title}");
                foreach (var f in result.Failures)
                {
                    sb.AppendLine($"      {Red}•{Reset} [{f.Rule}] {f.Message}");
                }
            }
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatSeedReconcileResult(SeedReconcileResult result)
    {
        if (result.NothingToDo)
            return $"{Green}Nothing to reconcile.{Reset}";

        var sb = new StringBuilder();
        sb.AppendLine($"{Bold}Seed Reconciliation{Reset}");
        sb.AppendLine(new string('─', 40));

        if (result.LinksRepaired > 0)
            sb.AppendLine($"  {Green}✔{Reset} Links repaired:   {result.LinksRepaired}");
        if (result.LinksRemoved > 0)
            sb.AppendLine($"  {Yellow}✔{Reset} Links removed:    {result.LinksRemoved}");
        if (result.ParentIdsFixed > 0)
            sb.AppendLine($"  {Green}✔{Reset} Parent IDs fixed:  {result.ParentIdsFixed}");

        foreach (var warning in result.Warnings)
            sb.AppendLine($"  {Yellow}⚠{Reset} {warning}");

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatSeedPublishResult(SeedPublishResult result)
    {
        return result.Status switch
        {
            SeedPublishStatus.Created =>
                $"{Green}Published seed #{result.OldId} as #{result.NewId}: {result.Title}{Reset}"
                + (result.LinkWarnings.Count > 0
                    ? Environment.NewLine + string.Join(Environment.NewLine, result.LinkWarnings.Select(w => $"  {Yellow}⚠{Reset} {w}"))
                    : ""),
            SeedPublishStatus.Skipped =>
                $"{Dim}Seed #{result.OldId} already published — skipped.{Reset}",
            SeedPublishStatus.DryRun =>
                $"{Cyan}[dry-run]{Reset} Would publish seed #{result.OldId}: {result.Title}",
            SeedPublishStatus.ValidationFailed =>
                $"{Red}✘{Reset} Seed #{result.OldId} failed validation: {result.Title}"
                + Environment.NewLine + string.Join(Environment.NewLine, result.ValidationFailures.Select(f => $"    {Red}•{Reset} [{f.Rule}] {f.Message}")),
            SeedPublishStatus.Error =>
                $"{Red}✘{Reset} Seed #{result.OldId}: {result.ErrorMessage}",
            _ => result.ErrorMessage ?? "Unknown status",
        };
    }

    public string FormatSeedPublishBatchResult(SeedPublishBatchResult result)
    {
        var sb = new StringBuilder();

        if (result.Results.Count == 0 && result.CycleErrors.Count == 0)
        {
            return $"{Dim}No seeds to publish.{Reset}";
        }

        sb.AppendLine($"{Bold}Seed Publish{Reset}");
        sb.AppendLine(new string('─', 40));

        foreach (var r in result.Results)
        {
            sb.AppendLine($"  {FormatSeedPublishResult(r)}");
        }

        foreach (var err in result.CycleErrors)
        {
            sb.AppendLine($"  {Red}⚠{Reset} {err}");
        }

        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"  Created: {result.CreatedCount}  Skipped: {result.SkippedCount}  Errors: {result.Results.Count(r => !r.IsSuccess)}");

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
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

    // ── Seed view helpers ───────────────────────────────────────────

    internal static string FormatSeedAge(DateTimeOffset? seedCreatedAt)
    {
        if (seedCreatedAt is null)
            return "?d ago";

        var elapsed = DateTimeOffset.UtcNow - seedCreatedAt.Value;
        if (elapsed.TotalDays >= 30)
            return $"{(int)(elapsed.TotalDays / 30)}mo ago";
        if (elapsed.TotalDays >= 14)
            return $"{(int)(elapsed.TotalDays / 7)}w ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    internal static int CountNonEmptyFields(WorkItem seed)
    {
        var filled = 0;
        foreach (var kvp in seed.Fields)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Value))
                filled++;
        }
        return filled;
    }

    /// <summary>Determines whether a seed is stale based on its age and the configured threshold.</summary>
    internal static bool IsStaleSeed(WorkItem seed, int staleDays)
    {
        return staleDays > 0 && seed.SeedCreatedAt.HasValue
            && (DateTimeOffset.UtcNow - seed.SeedCreatedAt.Value).TotalDays >= staleDays;
    }

    /// <summary>
    /// Formats a link annotation from the perspective of a given seed.
    /// E.g., "blocks -2", "depends on #12345", "related -3".
    /// </summary>
    internal static string FormatLinkAnnotation(int seedId, SeedLink link)
    {
        var otherId = link.SourceId == seedId ? link.TargetId : link.SourceId;
        var idLabel = otherId < 0 ? $"{otherId}" : $"#{otherId}";

        // When seed is the target, use the reverse link type for the label
        var effectiveType = link.SourceId == seedId
            ? link.LinkType
            : SeedLinkTypes.GetReverse(link.LinkType) ?? link.LinkType;

        return $"{FormatLinkTypeLabel(effectiveType)} {idLabel}";
    }

    private static string FormatLinkTypeLabel(string linkType) => linkType switch
    {
        SeedLinkTypes.Blocks => "blocks",
        SeedLinkTypes.BlockedBy => "blocked by",
        SeedLinkTypes.DependsOn => "depends on",
        SeedLinkTypes.DependedOnBy => "depended on by",
        SeedLinkTypes.Related => "related",
        SeedLinkTypes.ParentChild => "parent-child",
        _ => linkType,
    };

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

    /// <summary>
    /// Formats a flow-start summary with box-drawing characters for the ANSI fallback path.
    /// </summary>
    public string FormatFlowSummary(int id, string title, string originalState, string? newState, string? branchName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FormatSuccess($"Flow started for #{id} — {title}"));

        var rows = new List<(string Label, string Value)>();

        if (newState is not null)
        {
            var oldColor = GetStateColor(originalState);
            var newColor = GetStateColor(newState);
            rows.Add(("State", $"{oldColor}{originalState}{Reset} {Green}→{Reset} {newColor}{newState}{Reset}"));
        }
        else
        {
            var stateColor = GetStateColor(originalState);
            rows.Add(("State", $"{stateColor}{originalState}{Reset}"));
        }

        if (branchName is not null)
            rows.Add(("Branch", branchName));

        rows.Add(("Context", $"set to #{id}"));

        // Calculate max label width for alignment
        var maxLabel = 0;
        foreach (var (label, _) in rows)
        {
            if (label.Length > maxLabel)
                maxLabel = label.Length;
        }

        // Calculate max visible value width for border sizing
        var maxValueVisible = 0;
        foreach (var (label, value) in rows)
        {
            var visibleValue = StripAnsi(value);
            var lineWidth = maxLabel + 2 + visibleValue.Length; // "Label: Value"
            if (lineWidth > maxValueVisible)
                maxValueVisible = lineWidth;
        }

        var innerWidth = maxValueVisible + 2; // padding
        var header = " Summary ";
        var headerPad = innerWidth - header.Length;
        var headerLeft = headerPad / 2;
        var headerRight = headerPad - headerLeft;

        sb.Append("┌");
        sb.Append('─', headerLeft);
        sb.Append(Bold);
        sb.Append(header);
        sb.Append(Reset);
        sb.Append('─', headerRight);
        sb.AppendLine("┐");

        foreach (var (label, value) in rows)
        {
            sb.Append("│ ");
            sb.Append(Dim);
            sb.Append(label);
            sb.Append(':');
            sb.Append(Reset);
            sb.Append(new string(' ', maxLabel - label.Length + 1));
            sb.Append(value);
            var visibleValue = StripAnsi(value);
            var pad = innerWidth - (maxLabel + 2 + visibleValue.Length) - 2;
            if (pad > 0) sb.Append(' ', pad);
            sb.AppendLine(" │");
        }

        sb.Append("└");
        sb.Append('─', innerWidth);
        sb.Append("┘");

        return sb.ToString();
    }

    private static string StripAnsi(string input)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                // Skip ESC [ ... m
                i += 2;
                while (i < input.Length && input[i] != 'm')
                    i++;
                if (i < input.Length) i++; // skip 'm'
            }
            else
            {
                sb.Append(input[i]);
                i++;
            }
        }
        return sb.ToString();
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

    // Core fields excluded from extended display (already shown as dedicated lines)
    private static readonly HashSet<string> CoreFieldPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.WorkItemType", "System.Title", "System.State",
        "System.AssignedTo", "System.IterationPath", "System.AreaPath",
        "System.Rev", "System.TeamProject",
    };

    private static Dictionary<string, FieldDefinition> BuildFieldDefinitionLookup(
        IReadOnlyList<FieldDefinition>? definitions)
    {
        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        if (definitions is not null)
        {
            foreach (var def in definitions)
                lookup[def.ReferenceName] = def;
        }
        return lookup;
    }

    private static List<(string DisplayName, string Value)> GetExtendedFields(
        WorkItem item, Dictionary<string, FieldDefinition> defLookup,
        IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null)
    {
        var result = new List<(string, string)>();

        if (statusFieldEntries is not null)
        {
            foreach (var entry in statusFieldEntries)
            {
                if (!entry.IsIncluded)
                    continue;
                if (!item.Fields.TryGetValue(entry.ReferenceName, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                var displayName = defLookup.TryGetValue(entry.ReferenceName, out var def)
                    ? def.DisplayName
                    : ColumnResolver.DeriveDisplayName(entry.ReferenceName);
                var dataType = def?.DataType ?? "string";
                var formatted = FormatterHelpers.FormatFieldValue(value, dataType, maxWidth: 60);

                if (!string.IsNullOrWhiteSpace(formatted))
                    result.Add((displayName, formatted));
            }
            return result;
        }

        foreach (var kvp in item.Fields)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
                continue;
            if (CoreFieldPrefixes.Contains(kvp.Key))
                continue;

            var displayName = defLookup.TryGetValue(kvp.Key, out var def2)
                ? def2.DisplayName
                : ColumnResolver.DeriveDisplayName(kvp.Key);
            var dataType = def2?.DataType ?? "string";
            var formatted = FormatterHelpers.FormatFieldValue(kvp.Value, dataType, maxWidth: 60);

            if (!string.IsNullOrWhiteSpace(formatted))
                result.Add((displayName, formatted));
        }

        return result;
    }

}
