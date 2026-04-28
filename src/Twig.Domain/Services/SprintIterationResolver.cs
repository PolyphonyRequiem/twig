using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Resolves sprint iteration expressions to concrete <see cref="IterationPath"/> values
/// using the team iteration list from ADO, and aggregates work items across multiple iterations.
/// </summary>
public sealed class SprintIterationResolver(
    IIterationService iterationService,
    IWorkItemRepository workItemRepo)
{
    /// <summary>
    /// Resolves a single <see cref="IterationExpression"/> to an <see cref="IterationPath"/>.
    /// Returns <c>null</c> if the expression resolves to an out-of-bounds index or if
    /// no team iterations are available (for relative expressions).
    /// </summary>
    public async Task<IterationPath?> ResolveExpressionAsync(
        IterationExpression expression, CancellationToken ct = default)
    {
        if (expression.Kind == ExpressionKind.Absolute)
        {
            var parseResult = IterationPath.Parse(expression.Raw);
            return parseResult.IsSuccess ? parseResult.Value : null;
        }

        return await ResolveRelativeAsync(expression.Offset, ct);
    }

    /// <summary>
    /// Resolves a relative offset to an <see cref="IterationPath"/> by finding the current
    /// iteration in the sorted team iterations list and applying the offset.
    /// Returns <c>null</c> if out of bounds or no iterations exist.
    /// </summary>
    public async Task<IterationPath?> ResolveRelativeAsync(int offset, CancellationToken ct = default)
    {
        var teamIterations = await iterationService.GetTeamIterationsAsync(ct);

        if (teamIterations.Count == 0)
            return null;

        var currentIteration = await iterationService.GetCurrentIterationAsync(ct);

        // Sort by start date ascending (null dates sort to the end)
        var sorted = teamIterations
            .OrderBy(ti => ti.StartDate ?? DateTimeOffset.MaxValue)
            .ToList();

        // Find the index of the current iteration (by path, case-insensitive)
        var currentIndex = -1;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (string.Equals(sorted[i].Path, currentIteration.Value, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
            return null;

        var targetIndex = currentIndex + offset;

        if (targetIndex < 0 || targetIndex >= sorted.Count)
            return null;

        var targetPath = IterationPath.Parse(sorted[targetIndex].Path);
        return targetPath.IsSuccess ? targetPath.Value : null;
    }

    /// <summary>
    /// Resolves multiple expressions to concrete <see cref="IterationPath"/> values.
    /// Expressions that resolve to <c>null</c> (out of bounds, invalid) are silently skipped.
    /// Duplicate paths are deduplicated.
    /// </summary>
    public async Task<IReadOnlyList<IterationPath>> ResolveAllAsync(
        IReadOnlyList<IterationExpression> expressions, CancellationToken ct = default)
    {
        if (expressions.Count == 0)
            return [];

        var results = new List<IterationPath>(expressions.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var expr in expressions)
        {
            var resolved = await ResolveExpressionAsync(expr, ct);
            if (resolved is { } path && seen.Add(path.Value))
            {
                results.Add(path);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches work items across all resolved iterations, deduplicated by work item ID.
    /// When <paramref name="allUsers"/> is <c>false</c>, items are scoped to <paramref name="userDisplayName"/>.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> GetSprintItemsAsync(
        IReadOnlyList<IterationExpression> expressions,
        string? userDisplayName,
        bool allUsers,
        CancellationToken ct = default)
    {
        var resolvedPaths = await ResolveAllAsync(expressions, ct);

        if (resolvedPaths.Count == 0)
            return [];

        var seenIds = new HashSet<int>();
        var result = new List<WorkItem>();

        foreach (var path in resolvedPaths)
        {
            var items = allUsers || string.IsNullOrEmpty(userDisplayName)
                ? await workItemRepo.GetByIterationAsync(path, ct)
                : await workItemRepo.GetByIterationAndAssigneeAsync(path, userDisplayName, ct);

            foreach (var item in items)
            {
                if (seenIds.Add(item.Id))
                    result.Add(item);
            }
        }

        return result;
    }
}
