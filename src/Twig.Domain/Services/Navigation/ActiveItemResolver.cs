using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Services.Navigation;

/// <summary>
/// Resolves the active work item from context, cache, or ADO auto-fetch.
/// Consolidates the cache-hit → auto-fetch pattern used across commands.
/// </summary>
public sealed class ActiveItemResolver
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;

    public ActiveItemResolver(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService)
    {
        _contextStore = contextStore;
        _workItemRepo = workItemRepo;
        _adoService = adoService;
    }

    /// <summary>
    /// Resolves the active-context item: reads active ID from <see cref="IContextStore"/>,
    /// then attempts cache lookup, falling back to ADO auto-fetch on miss.
    /// </summary>
    public async Task<ActiveItemResult> GetActiveItemAsync(CancellationToken ct = default)
    {
        var activeId = await _contextStore.GetActiveWorkItemIdAsync(ct);
        if (activeId is null)
            return new ActiveItemResult.NoContext();

        return await ResolveByIdAsync(activeId.Value, ct);
    }

    /// <summary>
    /// Resolves a specific item by ID: cache lookup first, then ADO auto-fetch on miss.
    /// On successful auto-fetch, the item is cached before returning.
    /// </summary>
    public async Task<ActiveItemResult> ResolveByIdAsync(int id, CancellationToken ct = default)
    {
        var cached = await _workItemRepo.GetByIdAsync(id, ct);
        if (cached is not null)
            return new ActiveItemResult.Found(cached);

        try
        {
            var fetched = await _adoService.FetchAsync(id, ct);
            await _workItemRepo.SaveAsync(fetched, ct);
            return new ActiveItemResult.FetchedFromAdo(fetched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ActiveItemResult.Unreachable(id, ex.Message);
        }
    }
}
