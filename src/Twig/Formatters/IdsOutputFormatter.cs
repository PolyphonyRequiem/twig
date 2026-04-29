using System.Text;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;

namespace Twig.Formatters;

/// <summary>
/// Bare numeric IDs formatter — one ID per line, no decoration.
/// Designed for shell piping and scripting (e.g. <c>twig workspace -o ids | xargs ...</c>).
/// </summary>
public sealed class IdsOutputFormatter : IOutputFormatter
{
    public string FormatWorkItem(WorkItem item, bool showDirty) => item.Id.ToString();

    public string FormatTree(WorkTree tree, int maxDepth, int? activeId)
    {
        var sb = new StringBuilder();

        // Parent chain IDs (root → immediate parent)
        foreach (var parent in tree.ParentChain)
            sb.AppendLine(parent.Id.ToString());

        // Focused item
        sb.AppendLine(tree.FocusedItem.Id.ToString());

        // Children and descendants in depth-first order
        foreach (var child in tree.Children)
        {
            sb.AppendLine(child.Id.ToString());
            AppendDescendantsDepthFirst(sb, tree, child.Id, 2, maxDepth);
        }

        return TrimEnd(sb);
    }

    public string FormatWorkspace(Workspace ws, int staleDays)
    {
        if (ws.SprintItems.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in ws.SprintItems)
            sb.AppendLine(item.Id.ToString());

        return TrimEnd(sb);
    }

    public string FormatSprintView(Workspace ws, int staleDays)
        => FormatWorkspace(ws, staleDays);

    public string FormatQueryResults(QueryResult result)
    {
        if (result.Items.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in result.Items)
            sb.AppendLine(item.Id.ToString());

        return TrimEnd(sb);
    }

    // All non-list methods return empty string — not applicable for ids format
    public string FormatFieldChange(FieldChange change) => string.Empty;
    public string FormatError(string message) => string.Empty;
    public string FormatSuccess(string message) => string.Empty;
    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches) => string.Empty;
    public string FormatHint(string hint) => string.Empty;
    public string FormatInfo(string message) => string.Empty;
    public string FormatSetConfirmation(WorkItem item) => item.Id.ToString();
    public string FormatBranchInfo(string branchName) => string.Empty;
    public string FormatPrStatus(int prId, string title, string status) => string.Empty;
    public string FormatAnnotatedLogEntry(string hash, string message, string? workItemType, string? workItemState, int? workItemId) => string.Empty;
    public string FormatStatusSummary(WorkItem item) => string.Empty;
    public string FormatSeedView(IReadOnlyList<SeedViewGroup> groups, int totalWritableFields, int staleDays, IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null) => string.Empty;
    public string FormatSeedLinks(IReadOnlyList<SeedLink> links) => string.Empty;
    public string FormatWorkItemLinks(IReadOnlyList<WorkItemLink> links) => string.Empty;
    public string FormatSeedValidation(IReadOnlyList<SeedValidationResult> results) => string.Empty;
    public string FormatSeedReconcileResult(SeedReconcileResult result) => string.Empty;
    public string FormatSeedPublishResult(SeedPublishResult result) => string.Empty;
    public string FormatSeedPublishBatchResult(SeedPublishBatchResult result) => string.Empty;
    public string FormatAreaView(AreaView areaView)
    {
        if (areaView.AreaItems.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var item in areaView.AreaItems)
            sb.AppendLine(item.Id.ToString());

        return TrimEnd(sb);
    }

    private static void AppendDescendantsDepthFirst(StringBuilder sb, WorkTree tree, int parentId, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth) return;

        var descendants = tree.GetDescendants(parentId);
        foreach (var desc in descendants)
        {
            sb.AppendLine(desc.Id.ToString());
            AppendDescendantsDepthFirst(sb, tree, desc.Id, currentDepth + 1, maxDepth);
        }
    }

    private static string TrimEnd(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length -= 1;
        if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length -= 1;
        return sb.ToString();
    }
}
