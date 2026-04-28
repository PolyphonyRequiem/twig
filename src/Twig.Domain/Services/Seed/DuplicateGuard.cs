using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Checks for existing work items with matching title, type, and parent
/// before creation, preventing duplicates during SDLC conductor retries.
/// </summary>
public static class DuplicateGuard
{
    /// <summary>
    /// Searches for an existing child work item under <paramref name="parentId"/>
    /// with the same title (case-insensitive) and type.
    /// Returns the matching item if found, or <c>null</c> if no duplicate exists.
    /// </summary>
    public static async Task<WorkItem?> FindExistingChildAsync(
        IAdoWorkItemService adoService,
        int parentId,
        string title,
        WorkItemType type,
        CancellationToken ct = default)
    {
        var escapedTitle = title.Replace("'", "''");
        var wiql = $"SELECT [System.Id] FROM WorkItems " +
                   $"WHERE [System.Parent] = {parentId} " +
                   $"AND [System.Title] = '{escapedTitle}' " +
                   $"AND [System.WorkItemType] = '{type.Value}'";

        var ids = await adoService.QueryByWiqlAsync(wiql, top: 1, ct);
        if (ids.Count == 0)
            return null;

        return await adoService.FetchAsync(ids[0], ct);
    }
}
