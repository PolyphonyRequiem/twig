using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Orchestrates a multi-field atomic PATCH on a work item: seed routing,
/// optimistic-concurrency PATCH with retry, pending-note flush, cache resync,
/// prompt-state write.
/// </summary>
/// <remarks>
/// <para>
/// Both <c>PatchCommand</c> and <c>MutationTools.Patch</c> route through this
/// workflow. Adapter responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Parse JSON input, resolve per-field HTML conversion via
///   <c>HtmlFieldFormatter</c> into <see cref="FieldChange"/>s.</item>
///   <item>For non-seed items: fetch the remote and (CLI only) run interactive
///   conflict detection. Pass the fetched <see cref="WorkItem"/> in.</item>
///   <item>Render the resulting <see cref="PatchOutcome"/>.</item>
/// </list>
/// <para>
/// Best-effort side-effects (auto-note-flush, cache resync, prompt-state write)
/// never fail the workflow — they accumulate into the success variant's
/// <c>Warnings</c>.
/// </para>
/// </remarks>
public sealed class PatchWorkflow(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>
    /// Applies <paramref name="changes"/> atomically to <paramref name="item"/>.
    /// </summary>
    /// <param name="item">The work item being patched.</param>
    /// <param name="changes">The field changes to apply (atomic — all or nothing
    /// for the remote PATCH path; sequential for the seed path).</param>
    /// <param name="remote">Pre-fetched remote work item, used as the basis for
    /// optimistic concurrency. Must be supplied when <paramref name="item"/> is
    /// not a seed; ignored for seeds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PatchOutcome> ExecuteAsync(
        WorkItem item,
        IReadOnlyList<FieldChange> changes,
        WorkItem? remote,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        if (item.IsSeed)
        {
            var seedProvider = new SeedMutationProvider(workItemRepo);
            foreach (var change in changes)
            {
                var seedResult = await seedProvider.UpdateFieldAsync(item.Id, change, ct);
                if (!seedResult.IsSuccess)
                    return new PatchOutcome.SeedFieldRejected(change.FieldName, seedResult.ErrorMessage ?? "unknown");
            }

            await TryWritePromptStateAsync(warnings);
            return new PatchOutcome.SeedPatched(item, changes, warnings);
        }

        if (remote is null)
            throw new ArgumentNullException(nameof(remote), "remote work item is required for non-seed patches.");

        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            return new PatchOutcome.ConflictAfterRetry();
        }
        catch (AdoException ex)
        {
            return new PatchOutcome.AdoUnreachable(ex.Message);
        }

        try
        {
            await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"auto-push notes failed: {ex.Message}");
        }

        WorkItem updated;
        try
        {
            updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
            warnings.Add($"cache may be stale after patch — run 'twig sync' to resync ({ex.Message})");
        }

        await TryWritePromptStateAsync(warnings);

        return new PatchOutcome.Patched(updated, changes, warnings);
    }

    private async Task TryWritePromptStateAsync(List<string> warnings)
    {
        if (promptStateWriter is null) return;
        try
        {
            await promptStateWriter.WritePromptStateAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"prompt-state write failed: {ex.Message}");
        }
    }
}
