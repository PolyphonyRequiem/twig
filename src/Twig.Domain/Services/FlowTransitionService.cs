using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Encapsulates item resolution and state transition logic shared by FlowDone and FlowClose commands.
/// Accepts <see cref="ActiveItemResolver"/>, <see cref="IAdoWorkItemService"/>,
/// <see cref="IProcessConfigurationProvider"/>, and <see cref="ProtectedCacheWriter"/>.
/// </summary>
public sealed class FlowTransitionService
{
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly ProtectedCacheWriter _protectedCacheWriter;

    public FlowTransitionService(
        ActiveItemResolver activeItemResolver,
        IAdoWorkItemService adoService,
        IProcessConfigurationProvider processConfigProvider,
        ProtectedCacheWriter protectedCacheWriter)
    {
        _activeItemResolver = activeItemResolver;
        _adoService = adoService;
        _processConfigProvider = processConfigProvider;
        _protectedCacheWriter = protectedCacheWriter;
    }

    /// <summary>
    /// Resolves a work item by explicit ID or from active context.
    /// Returns a <see cref="FlowResolveResult"/> indicating success/failure.
    /// </summary>
    public async Task<FlowResolveResult> ResolveItemAsync(int? id, CancellationToken ct = default)
    {
        ActiveItemResult result;
        bool isExplicitId;

        if (id.HasValue)
        {
            result = await _activeItemResolver.ResolveByIdAsync(id.Value, ct);
            isExplicitId = true;
        }
        else
        {
            result = await _activeItemResolver.GetActiveItemAsync(ct);
            isExplicitId = false;
        }

        if (result.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            return FlowResolveResult.Success(item, isExplicitId);
        }

        var errorMessage = errorId is not null
            ? $"Work item #{errorId} could not be fetched: {errorReason}"
            : "No active work item. Run 'twig flow-start <id>' first.";

        return FlowResolveResult.Error(errorMessage);
    }

    /// <summary>
    /// Transitions a work item to the target state category, with an optional fallback category.
    /// Re-fetches the item from ADO to get the latest revision, patches the state, and saves through
    /// <see cref="ProtectedCacheWriter"/>.
    /// </summary>
    public async Task<FlowTransitionResult> TransitionStateAsync(
        WorkItem item,
        StateCategory targetCategory,
        StateCategory? fallbackCategory = null,
        CancellationToken ct = default)
    {
        var processConfig = _processConfigProvider.GetConfiguration();
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
        {
            return new FlowTransitionResult
            {
                Transitioned = false,
                OriginalState = item.State,
                AlreadyInTargetCategory = false,
            };
        }

        var currentCategory = StateCategoryResolver.Resolve(item.State, typeConfig.StateEntries);

        // Skip if already in target or fallback category
        if (currentCategory == targetCategory ||
            (fallbackCategory.HasValue && currentCategory == fallbackCategory.Value))
        {
            return new FlowTransitionResult
            {
                Transitioned = false,
                OriginalState = item.State,
                AlreadyInTargetCategory = true,
            };
        }

        // Try target category first, then fallback
        var resolveResult = StateResolver.ResolveByCategory(targetCategory, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess && fallbackCategory.HasValue)
            resolveResult = StateResolver.ResolveByCategory(fallbackCategory.Value, typeConfig.StateEntries);

        if (!resolveResult.IsSuccess)
        {
            return new FlowTransitionResult
            {
                Transitioned = false,
                OriginalState = item.State,
                AlreadyInTargetCategory = false,
            };
        }

        var newState = resolveResult.Value;
        var originalState = item.State;
        var remote = await _adoService.FetchAsync(item.Id, ct);
        var changes = new[] { new FieldChange("System.State", originalState, newState) };
        var newRevision = await _adoService.PatchAsync(item.Id, changes, remote.Revision, ct);

        item.ChangeState(newState);
        item.MarkSynced(newRevision);
        await _protectedCacheWriter.SaveProtectedAsync(item, ct);

        return new FlowTransitionResult
        {
            Transitioned = true,
            OriginalState = originalState,
            NewState = newState,
            AlreadyInTargetCategory = false,
        };
    }
}

/// <summary>Result of resolving a work item for a flow command.</summary>
public sealed class FlowResolveResult
{
    public bool IsSuccess { get; private init; }
    public WorkItem? Item { get; private init; }
    public bool IsExplicitId { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static FlowResolveResult Success(WorkItem item, bool isExplicitId) => new()
    {
        IsSuccess = true,
        Item = item,
        IsExplicitId = isExplicitId,
    };

    public static FlowResolveResult Error(string message) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
    };
}

/// <summary>Result of a state transition attempt.</summary>
public sealed class FlowTransitionResult
{
    public bool Transitioned { get; init; }
    public string OriginalState { get; init; } = string.Empty;
    public string? NewState { get; init; }
    public bool AlreadyInTargetCategory { get; init; }
}
