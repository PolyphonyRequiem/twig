using System.Text;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;

namespace Twig.Formatters;

/// <summary>
/// Minimal, single-line, no-ANSI formatter designed for piping and scripting.
/// Uses section prefixes: CTX, SPR, SEED.
/// </summary>
public sealed class MinimalOutputFormatter : IOutputFormatter
{
    public string FormatStatusSummary(WorkItem item) => string.Empty;

    public string FormatWorkItem(WorkItem item, bool showDirty)
    {
        var dirty = showDirty && item.IsDirty ? " *" : "";
        var assigned = item.AssignedTo is not null ? $" @{item.AssignedTo}" : "";
        return $"#{item.Id} {item.State} \"{item.Title}\" {item.Type}{assigned}{dirty}";
    }

    // activeId is accepted per the IOutputFormatter contract but not used —
    // minimal output marks the focused item with ">" instead.
    public string FormatTree(WorkTree tree, int maxChildren, int? activeId)
    {
        var sb = new StringBuilder();
        var focusDepth = tree.ParentChain.Count;

        // Parent chain with depth-based indentation
        for (var i = 0; i < tree.ParentChain.Count; i++)
        {
            var parent = tree.ParentChain[i];
            var indent = new string(' ', i * 2);
            var stateLabel = FormatterHelpers.GetStateLabel(parent.State);
            sb.AppendLine($"{indent}  #{parent.Id} [{stateLabel}] {parent.Title}");
        }

        // Focused item (marked with >) at its natural depth
        var focusIndent = new string(' ', focusDepth * 2);
        var focusLabel = FormatterHelpers.GetStateLabel(tree.FocusedItem.State);
        var focusDirty = tree.FocusedItem.IsDirty ? " *" : "";
        sb.AppendLine($"{focusIndent}> #{tree.FocusedItem.Id} [{focusLabel}] {tree.FocusedItem.Title}{focusDirty}");

        // Children at focused+1 depth
        var childIndent = new string(' ', (focusDepth + 1) * 2);
        var displayCount = Math.Min(tree.Children.Count, maxChildren);
        var hasMore = tree.Children.Count > maxChildren;
        for (var i = 0; i < displayCount; i++)
        {
            var child = tree.Children[i];
            var stateLabel = FormatterHelpers.GetStateLabel(child.State);
            var dirty = child.IsDirty ? " *" : "";
            sb.AppendLine($"{childIndent}#{child.Id} [{stateLabel}] {child.Title}{dirty}");
        }

        if (hasMore)
            sb.AppendLine($"{childIndent}... +{tree.Children.Count - maxChildren} more");

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

        // Context
        if (ws.ContextItem is not null)
        {
            var dirty = ws.ContextItem.IsDirty ? " *" : "";
            sb.AppendLine($"CTX #{ws.ContextItem.Id} {ws.ContextItem.State} \"{ws.ContextItem.Title}\"{dirty}");
        }
        else
        {
            sb.AppendLine("CTX (none)");
        }

        // Sprint items
        foreach (var item in ws.SprintItems)
        {
            var dirty = item.IsDirty ? " *" : "";
            var stateLabel = FormatterHelpers.GetStateLabel(item.State);
            sb.AppendLine($"SPR #{item.Id} [{stateLabel}] {item.Title}{dirty}");
        }

        // Seeds
        var staleSeeds = ws.GetStaleSeeds(staleDays);
        var staleSeedIds = new HashSet<int>(staleSeeds.Count);
        foreach (var s in staleSeeds)
            staleSeedIds.Add(s.Id);
        foreach (var seed in ws.Seeds)
        {
            var staleWarning = staleSeedIds.Contains(seed.Id) ? " STALE" : "";
            sb.AppendLine($"SEED #{seed.Id} {seed.Title} ({seed.Type}){staleWarning}");
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

        // Context
        if (ws.ContextItem is not null)
        {
            var dirty = ws.ContextItem.IsDirty ? " *" : "";
            sb.AppendLine($"CTX #{ws.ContextItem.Id} {ws.ContextItem.State} \"{ws.ContextItem.Title}\"{dirty}");
        }
        else
        {
            sb.AppendLine("CTX (none)");
        }

        // Sprint items with assignee prefix
        foreach (var item in ws.SprintItems)
        {
            var dirty = item.IsDirty ? " *" : "";
            var stateLabel = FormatterHelpers.GetStateLabel(item.State);
            var assigned = item.AssignedTo is not null ? $" @{item.AssignedTo}" : "";
            sb.AppendLine($"SPR #{item.Id} [{stateLabel}] {item.Title}{assigned}{dirty}");
        }

        // Seeds
        var staleSeeds = ws.GetStaleSeeds(staleDays);
        var staleSeedIds = new HashSet<int>(staleSeeds.Count);
        foreach (var s in staleSeeds)
            staleSeedIds.Add(s.Id);
        foreach (var seed in ws.Seeds)
        {
            var staleWarning = staleSeedIds.Contains(seed.Id) ? " STALE" : "";
            sb.AppendLine($"SEED #{seed.Id} {seed.Title} ({seed.Type}){staleWarning}");
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatFieldChange(FieldChange change)
    {
        return $"{change.FieldName}: {change.OldValue ?? ""} -> {change.NewValue ?? ""}";
    }

    public string FormatError(string message)
    {
        return $"error: {message}";
    }

    public string FormatSuccess(string message)
    {
        return message;
    }

    public string FormatHint(string hint)
    {
        return "";
    }

    public string FormatInfo(string message)
    {
        return message;
    }

    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches)
    {
        var sb = new StringBuilder();
        foreach (var (id, title) in matches)
        {
            sb.AppendLine($"#{id} \"{title}\"");
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            sb.Length -= 1;

        return sb.ToString();
    }

    public string FormatBranchInfo(string branchName)
    {
        return $"BRANCH {branchName}";
    }

    public string FormatPrStatus(int prId, string title, string status)
    {
        return $"PR !{prId} {status} \"{title}\"";
    }

    public string FormatAnnotatedLogEntry(string hash, string message, string? workItemType, string? workItemState, int? workItemId)
    {
        var shortHash = hash.Length > 7 ? hash[..7] : hash;
        if (workItemId.HasValue)
            return $"{shortHash} {message} #{workItemId} {workItemType ?? ""} {workItemState ?? ""}".TrimEnd();

        return $"{shortHash} {message}";
    }

}
