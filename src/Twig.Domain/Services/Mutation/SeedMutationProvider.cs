using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Mutation provider for local-only seed work items.
/// Writes field/state changes directly to the local SQLite cache via
/// <see cref="IWorkItemRepository"/> — no ADO calls, no conflict resolution.
/// </summary>
public sealed class SeedMutationProvider(IWorkItemRepository workItemRepo) : IMutationProvider
{
    public async Task<MutationResult> UpdateFieldAsync(int itemId, FieldChange change, CancellationToken ct)
    {
        var item = await workItemRepo.GetByIdAsync(itemId, ct);
        if (item is null)
            return MutationResult.Error($"Work item {itemId} not found.");

        if (!item.IsSeed)
            return MutationResult.Error($"Work item {itemId} is not a seed. Use the ADO mutation provider.");

        item.UpdateField(change.FieldName, change.NewValue);
        await workItemRepo.SaveAsync(item, ct);
        return MutationResult.Success(item.Revision);
    }

    public async Task<MutationResult> ChangeStateAsync(int itemId, FieldChange stateChange, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateChange.NewValue))
            return MutationResult.Error("State value cannot be null or empty.");

        var item = await workItemRepo.GetByIdAsync(itemId, ct);
        if (item is null)
            return MutationResult.Error($"Work item {itemId} not found.");

        if (!item.IsSeed)
            return MutationResult.Error($"Work item {itemId} is not a seed. Use the ADO mutation provider.");

        item.ChangeState(stateChange.NewValue);
        await workItemRepo.SaveAsync(item, ct);
        return MutationResult.Success(item.Revision);
    }
}
