using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Creates seed work items, validating parent/child rules via <see cref="ProcessConfiguration"/>
/// and inheriting area/iteration paths from the parent context.
/// </summary>
public static class SeedFactory
{
    /// <summary>
    /// Creates a seed work item under the given parent context.
    /// </summary>
    /// <param name="title">Title for the new seed.</param>
    /// <param name="parentContext">Optional parent work item — used for type inference and path inheritance.</param>
    /// <param name="processConfig">Process configuration for validating parent/child rules.</param>
    /// <param name="typeOverride">Explicit child type. If null, inferred from parent's allowed child types.</param>
    /// <param name="assignedTo">Optional user display name to auto-assign the seed.</param>
    public static Result<WorkItem> Create(
        string title,
        WorkItem? parentContext,
        ProcessConfiguration processConfig,
        WorkItemType? typeOverride = null,
        string? assignedTo = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Fail<WorkItem>("Seed title cannot be empty.");

        // Determine the child type
        WorkItemType childType;

        if (parentContext is null)
        {
            // No parent — explicit type is required
            if (typeOverride is null)
                return Result.Fail<WorkItem>("Explicit type is required when no parent context is provided.");

            childType = typeOverride.Value;
        }
        else
        {
            var allowedChildren = processConfig.GetAllowedChildTypes(parentContext.Type);

            if (typeOverride is not null)
            {
                // Validate explicit override is allowed
                if (!ContainsType(allowedChildren, typeOverride.Value))
                    return Result.Fail<WorkItem>(
                        $"Type '{typeOverride.Value}' is not an allowed child of '{parentContext.Type}'.");

                childType = typeOverride.Value;
            }
            else
            {
                // Infer default child type
                if (allowedChildren.Count == 0)
                    return Result.Fail<WorkItem>(
                        $"Type '{parentContext.Type}' does not allow child items.");

                childType = allowedChildren[0];
            }
        }

        // Create the seed, inheriting area/iteration paths and parent from the context
        var seed = WorkItem.CreateSeed(
            childType,
            title,
            parentContext?.Id,
            parentContext?.AreaPath ?? default,
            parentContext?.IterationPath ?? default,
            assignedTo);

        return Result.Ok(seed);
    }

    /// <summary>
    /// Creates an unparented seed with explicit area/iteration paths.
    /// Used by <c>twig new</c> for top-level work items (e.g., Epics).
    /// </summary>
    public static Result<WorkItem> CreateUnparented(
        string title,
        WorkItemType type,
        AreaPath areaPath,
        IterationPath iterationPath,
        string? assignedTo = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Fail<WorkItem>("Title cannot be empty.");

        var seed = WorkItem.CreateSeed(
            type,
            title,
            parentId: null,
            areaPath,
            iterationPath,
            assignedTo);

        return Result.Ok(seed);
    }

    private static bool ContainsType(IReadOnlyList<WorkItemType> types, WorkItemType target)
    {
        foreach (var t in types)
        {
            if (t == target)
                return true;
        }

        return false;
    }
}
