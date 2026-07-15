using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Ado;

public static class ConflictRetryHelper
{
    /// <summary>
    /// Attempts to patch a work item; on <see cref="AdoConflictException"/>
    /// re-fetches the current revision and retries exactly once.
    /// A second conflict is rethrown for the caller to handle.
    /// </summary>
    /// <returns>The new revision number after a successful patch.</returns>
    public static async Task<int> PatchWithRetryAsync(
        IAdoWorkItemService adoService,
        int itemId,
        IReadOnlyList<FieldChange> changes,
        int expectedRevision,
        CancellationToken ct) =>
        await PatchWithRetryCoreAsync(
            adoService,
            itemId,
            () => changes,
            _ => changes,
            expectedRevision,
            ct);

    internal static async Task<int> PatchWithRetryAsync(
        IAdoWorkItemService adoService,
        WorkItem currentItem,
        Func<WorkItem, IReadOnlyList<FieldChange>> changesFactory,
        int expectedRevision,
        CancellationToken ct) =>
        await PatchWithRetryCoreAsync(
            adoService,
            currentItem.Id,
            () => changesFactory(currentItem),
            changesFactory,
            expectedRevision,
            ct);

    private static async Task<int> PatchWithRetryCoreAsync(
        IAdoWorkItemService adoService,
        int itemId,
        Func<IReadOnlyList<FieldChange>> initialChangesFactory,
        Func<WorkItem, IReadOnlyList<FieldChange>> retryChangesFactory,
        int expectedRevision,
        CancellationToken ct)
    {
        try
        {
            return await adoService.PatchAsync(
                itemId,
                initialChangesFactory(),
                expectedRevision,
                ct);
        }
        catch (AdoConflictException)
        {
        }

        var freshItem = await adoService.FetchAsync(itemId, ct);
        return await adoService.PatchAsync(
            freshItem.Id,
            retryChangesFactory(freshItem),
            freshItem.Revision,
            ct);
    }
}
