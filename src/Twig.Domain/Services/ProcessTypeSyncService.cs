using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Fetches work item types and process configuration from ADO, infers parent-child
/// relationships, and persists <see cref="ProcessTypeRecord"/> objects into the store.
/// Static class with method parameters (not constructor-injected) because callers
/// may not have DI-resolved services available at call time.
/// </summary>
public static class ProcessTypeSyncService
{
    /// <summary>
    /// Fetches type metadata from <paramref name="iterationService"/>, infers the backlog
    /// hierarchy, and persists each type as a <see cref="ProcessTypeRecord"/> via
    /// <paramref name="processTypeStore"/>. Returns the count of types synced.
    /// Does not catch exceptions — callers handle errors.
    /// </summary>
    public static async Task<int> SyncAsync(
        IIterationService iterationService,
        IProcessTypeStore processTypeStore,
        CancellationToken ct = default)
    {
        var typesWithStates = await iterationService.GetWorkItemTypesWithStatesAsync(ct)
            ?? Array.Empty<WorkItemTypeWithStates>();

        var processConfig = await iterationService.GetProcessConfigurationAsync(ct)
            ?? new ProcessConfigurationData();

        var parentChildMap = BacklogHierarchyService.InferParentChildMap(processConfig);

        foreach (var wit in typesWithStates)
        {
            parentChildMap.TryGetValue(wit.Name, out var children);
            var defaultChild = children is { Count: > 0 } ? children[0] : null;

            await processTypeStore.SaveAsync(new ProcessTypeRecord
            {
                TypeName = wit.Name,
                States = wit.States.Select(s => new StateEntry(s.Name, StateCategoryResolver.ParseCategory(s.Category), s.Color)).ToList(),
                DefaultChildType = defaultChild,
                ValidChildTypes = children ?? [],
                ColorHex = wit.Color,
                IconId = wit.IconId,
            }, ct);
        }

        await processTypeStore.SaveProcessConfigurationDataAsync(processConfig, ct);

        return typesWithStates.Count;
    }
}
