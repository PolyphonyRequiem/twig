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
            (id, patch, revision, token) => adoService.PatchAsync(id, patch, revision, token),
            adoService.FetchAsync,
            itemId,
            () => changes,
            _ => changes,
            expectedRevision,
            ct);

    /// <summary>
    /// Provider-boundary form of <see cref="PatchWithRetryAsync(IAdoWorkItemService, int, IReadOnlyList{FieldChange}, int, CancellationToken)"/>.
    /// The cached integer revision is converted to an opaque precondition by the ADO codec
    /// before any call: a revision of zero or below is refused as missing, so it is never
    /// sent, never read as a conflict and never retried.
    /// </summary>
    /// <returns>The new revision number after a successful patch.</returns>
    /// <exception cref="ProviderRevisionPreconditionRefusedException">
    /// The precondition is missing, foreign or non-canonical. Raised before any transport call.
    /// </exception>
    public static async Task<int> PatchWithRetryAsync(
        IProviderWorkItemWriter adoService,
        int itemId,
        IReadOnlyList<FieldChange> changes,
        int expectedRevision,
        CancellationToken ct) =>
        await PatchWithRetryCoreAsync(
            ToRevisionPatch(adoService),
            adoService.FetchAsync,
            itemId,
            () => changes,
            _ => changes,
            expectedRevision,
            ct);

    internal static async Task<int> PatchWithRetryAsync(
        IProviderWorkItemWriter adoService,
        WorkItem currentItem,
        Func<WorkItem, IReadOnlyList<FieldChange>> changesFactory,
        int expectedRevision,
        CancellationToken ct) =>
        await PatchWithRetryCoreAsync(
            ToRevisionPatch(adoService),
            adoService.FetchAsync,
            currentItem.Id,
            () => changesFactory(currentItem),
            changesFactory,
            expectedRevision,
            ct);

    private static Func<int, IReadOnlyList<FieldChange>, int, CancellationToken, Task<int>> ToRevisionPatch(
        IProviderWorkItemWriter writer)
    {
        var codec = AdoProviderRevisionCodec.Instance;
        return async (id, changes, revision, ct) =>
        {
            var expected = codec.FromCacheRevision(revision);
            var written = await writer.PatchAsync(id, changes, expected, ct).ConfigureAwait(false);
            return codec.ToCacheRevision(written);
        };
    }

    private static async Task<int> PatchWithRetryCoreAsync(
        Func<int, IReadOnlyList<FieldChange>, int, CancellationToken, Task<int>> patch,
        Func<int, CancellationToken, Task<WorkItem>> fetch,
        int itemId,
        Func<IReadOnlyList<FieldChange>> initialChangesFactory,
        Func<WorkItem, IReadOnlyList<FieldChange>> retryChangesFactory,
        int expectedRevision,
        CancellationToken ct)
    {
        try
        {
            return await patch(
                itemId,
                initialChangesFactory(),
                expectedRevision,
                ct);
        }
        catch (AdoConflictException)
        {
        }

        var freshItem = await fetch(itemId, ct);
        return await patch(
            freshItem.Id,
            retryChangesFactory(freshItem),
            freshItem.Revision,
            ct);
    }
}