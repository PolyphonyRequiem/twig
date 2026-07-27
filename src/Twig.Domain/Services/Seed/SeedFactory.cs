using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Creates seed work items, validating parent/child rules via <see cref="ProcessConfiguration"/>
/// and inheriting area/iteration paths from the parent context.
/// <para>
/// Wayfinder 0014: identity is <b>minted</b>, not allocated. The caller passes the
/// <see cref="StagedSeedIdentity"/> it obtained from <see cref="IStagedIdentityRegistry"/>;
/// there is no counter to initialize, so there is no two-line preamble a sixth call site can
/// forget (0003 §2). The negative integer lands on <see cref="WorkItem.Id"/> as a display
/// alias only — <see cref="WorkItem.StagedIdentity"/> is the key.
/// </para>
/// </summary>
public sealed class SeedFactory
{
    /// <summary>
    /// Creates a seed work item under the given parent context.
    /// </summary>
    /// <param name="title">Title for the new seed.</param>
    /// <param name="parentContext">Optional parent work item — used for type inference and path inheritance.</param>
    /// <param name="processConfig">Process configuration for validating parent/child rules.</param>
    /// <param name="typeOverride">Explicit child type. If null, inferred from parent's allowed child types.</param>
    /// <param name="assignedTo">Optional user display name to auto-assign the seed.</param>
    /// <param name="identity">The minted identity and display alias for the new seed.</param>
    public Result<WorkItem> Create(
        string title,
        WorkItem? parentContext,
        ProcessConfiguration processConfig,
        StagedSeedIdentity identity,
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
                if (!processConfig.IsChildTypeAllowed(parentContext.Type, typeOverride.Value))
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
        var seed = CreateSeedInternal(
            childType,
            title,
            parentContext?.Id,
            parentContext?.AreaPath ?? default,
            parentContext?.IterationPath ?? default,
            assignedTo,
            identity);

        return Result.Ok(seed);
    }

    /// <summary>
    /// Creates a seed with explicit area/iteration paths.
    /// Used by <c>twig new</c>. Pass <paramref name="parentId"/> to create a child item.
    /// </summary>
    public Result<WorkItem> CreateUnparented(
        string title,
        WorkItemType type,
        AreaPath areaPath,
        IterationPath iterationPath,
        StagedSeedIdentity identity,
        string? assignedTo = null,
        int? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Fail<WorkItem>("Title cannot be empty.");

        var seed = CreateSeedInternal(
            type,
            title,
            parentId,
            areaPath,
            iterationPath,
            assignedTo,
            identity);

        return Result.Ok(seed);
    }

    private static WorkItem CreateSeedInternal(
        WorkItemType type,
        string title,
        int? parentId,
        AreaPath areaPath,
        IterationPath iterationPath,
        string? assignedTo,
        StagedSeedIdentity identity)
    {
        var seed = new WorkItem
        {
            // The negative integer is the DISPLAY ALIAS (0003 §5a) — never a key, never joined
            // on, never a FK target. StagedIdentity below is the key.
            Id = identity.Alias.Value,
            StagedIdentity = identity.Identity,
            Type = type,
            Title = title,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = parentId,
            AreaPath = areaPath,
            IterationPath = iterationPath,
            AssignedTo = assignedTo,
        };

        if (!string.IsNullOrWhiteSpace(assignedTo))
            seed.SetField("System.AssignedTo", assignedTo);

        return seed;
    }
}
