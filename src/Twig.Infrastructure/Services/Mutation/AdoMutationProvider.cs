using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Mutation provider for published (non-seed) work items.
/// Applies field and state changes via the ADO REST API with optimistic concurrency
/// retry, auto-pushes pending notes, and refreshes the local cache after mutation.
/// </summary>
/// <remarks>
/// This provider encapsulates the common mutation flow extracted from
/// <c>UpdateCommand</c> and <c>StateCommand</c>:
/// fetch remote → patch with retry → auto-push notes → resync cache.
/// Command-level concerns (conflict resolution UI, parent propagation,
/// state transition validation) remain at the command level.
/// </remarks>
public sealed class AdoMutationProvider(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore) : IMutationProvider
{
    public async Task<MutationResult> UpdateFieldAsync(int itemId, FieldChange change, CancellationToken ct)
    {
        var remote = await adoService.FetchAsync(itemId, ct);

        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(
                adoService, itemId, [change], remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            return MutationResult.Error("Concurrency conflict after retry. Run 'twig sync' and retry.");
        }

        await PushNotesBestEffortAsync(itemId);

        var updated = await adoService.FetchAsync(itemId, ct);
        await workItemRepo.SaveAsync(updated, ct);

        return MutationResult.Success(updated.Revision);
    }

    public async Task<MutationResult> ChangeStateAsync(int itemId, FieldChange stateChange, CancellationToken ct)
    {
        var remote = await adoService.FetchAsync(itemId, ct);

        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(
                adoService, itemId, [stateChange], remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            return MutationResult.Error("Concurrency conflict after retry. Run 'twig sync' and retry.");
        }

        await PushNotesBestEffortAsync(itemId);

        var updated = await adoService.FetchAsync(itemId, ct);
        await workItemRepo.SaveAsync(updated, ct);

        return MutationResult.Success(updated.Revision);
    }

    /// <summary>
    /// Flushes locally-staged notes as ADO comments. Best-effort — failures
    /// are swallowed so the primary mutation result is never affected.
    /// </summary>
    private async Task PushNotesBestEffortAsync(int itemId)
    {
        try
        {
            await AutoPushNotesHelper.PushAndClearAsync(itemId, pendingChangeStore, adoService);
        }
        catch
        {
            // Best-effort: note push failure must not affect the mutation result.
        }
    }
}
