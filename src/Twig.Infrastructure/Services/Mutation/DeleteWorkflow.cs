using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Orchestrates a two-phase work-item delete: a fresh ADO fetch with the
/// link guard (<see cref="PrepareAsync"/>) followed by the destructive
/// audit + delete + cache cleanup (<see cref="ExecuteAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both <c>DeleteCommand</c> and <c>MutationTools.Delete</c> route through
/// this workflow. Adapter responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Resolve the work item (CLI uses <c>ActiveItemResolver</c>; MCP uses
///   <c>WorkspaceContext.FetchWithFallbackAsync</c>).</item>
///   <item>Reject seeds with the adapter-appropriate error shape.</item>
///   <item>Drive the confirmation UX (CLI: interactive prompt; MCP: two-call
///   <c>confirmed</c> protocol).</item>
///   <item>Render the resulting <see cref="DeletePreparation"/> /
///   <see cref="DeleteOutcome"/> variant.</item>
/// </list>
/// </remarks>
public sealed class DeleteWorkflow(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    IPendingChangeStore pendingChangeStore,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>
    /// Fetches the fresh item + links from ADO and applies the link guard.
    /// </summary>
    public async Task<DeletePreparation> PrepareAsync(int id, CancellationToken ct = default)
    {
        WorkItem freshItem;
        IReadOnlyList<WorkItemLink> links;
        IReadOnlyList<WorkItem> children;
        try
        {
            (freshItem, links) = await adoService.FetchWithLinksAsync(id, ct);
            children = await adoService.FetchChildrenAsync(id, ct);
        }
        catch (AdoException ex)
        {
            return new DeletePreparation.FetchFailed(ex.Message);
        }

        var (totalLinkCount, summary) = BuildLinkSummary(freshItem.ParentId, children.Count, links);
        if (totalLinkCount > 0)
            return new DeletePreparation.BlockedByLinks(freshItem, totalLinkCount, summary);

        return new DeletePreparation.Ready(freshItem);
    }

    /// <summary>
    /// Performs the destructive delete: best-effort audit comment on parent,
    /// ADO delete, cache cleanup, prompt-state refresh.
    /// </summary>
    public async Task<DeleteOutcome> ExecuteAsync(WorkItem freshItem, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        if (freshItem.ParentId.HasValue)
        {
            try
            {
                await adoService.AddCommentAsync(
                    freshItem.ParentId.Value,
                    $"Child work item #{freshItem.Id} '{freshItem.Title}' ({freshItem.Type}) was deleted via twig.",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Failed to write audit comment on parent #{freshItem.ParentId.Value}: {ex.Message}");
            }
        }

        try
        {
            await adoService.DeleteAsync(freshItem.Id, ct);
        }
        catch (AdoException ex)
        {
            return new DeleteOutcome.AdoFailed(ex.Message);
        }

        await workItemRepo.DeleteByIdAsync(freshItem.Id, ct);
        await linkRepo.SaveLinksAsync(freshItem.Id, Array.Empty<WorkItemLink>(), ct);
        await pendingChangeStore.ClearChangesAsync(freshItem.Id, ct);

        if (promptStateWriter is not null)
        {
            try
            {
                await promptStateWriter.WritePromptStateAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Failed to write prompt state: {ex.Message}");
            }
        }

        return new DeleteOutcome.Deleted(freshItem, warnings);
    }

    private static (int Total, string Summary) BuildLinkSummary(
        int? parentId,
        int childCount,
        IReadOnlyList<WorkItemLink> nonHierarchyLinks)
    {
        var parts = new List<string>();
        var total = 0;

        if (parentId.HasValue)
        {
            parts.Add("1 parent");
            total++;
        }

        if (childCount > 0)
        {
            parts.Add($"{childCount} child{(childCount != 1 ? "ren" : "")}");
            total += childCount;
        }

        var linksByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in nonHierarchyLinks)
        {
            linksByType.TryGetValue(link.LinkType, out var count);
            linksByType[link.LinkType] = count + 1;
        }

        foreach (var (linkType, count) in linksByType)
        {
            parts.Add($"{count} {linkType.ToLowerInvariant()}");
            total += count;
        }

        return (total, string.Join(", ", parts));
    }
}
