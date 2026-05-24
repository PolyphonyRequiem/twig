using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Orchestrates a single-field update against a published (non-seed) work item:
/// append-against-remote, optimistic-concurrency PATCH with retry, pending-note
/// flush, cache resync, prompt-state write.
/// </summary>
/// <remarks>
/// <para>
/// Both <c>UpdateCommand</c> and <c>MutationTools.Update</c> route through this
/// workflow. Adapter responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Parse arguments, resolve the value source (inline / file / stdin),
///         resolve HTML-conversion via <see cref="HtmlFieldFormatter"/>.</item>
///   <item>Branch local-only seed mutations to <see cref="SeedMutationProvider"/>.</item>
///   <item>Fetch the remote work item and (CLI only) run interactive
///         conflict detection. Pass the resulting <see cref="WorkItem"/> in.</item>
///   <item>Render the resulting <see cref="FieldUpdateOutcome"/>.</item>
/// </list>
/// <para>
/// Best-effort side-effects (auto-note-flush, cache resync, prompt-state write)
/// never fail the workflow — they accumulate into
/// <see cref="FieldUpdateOutcome.Succeeded.Warnings"/>.
/// </para>
/// </remarks>
public sealed class FieldUpdateWorkflow(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>
    /// Patches a single field on <paramref name="local"/>. Returns
    /// <see cref="FieldUpdateOutcome.ConflictAfterRetry"/> if the optimistic-
    /// concurrency retry is exhausted; otherwise <see cref="FieldUpdateOutcome.Succeeded"/>.
    /// </summary>
    /// <param name="local">The work item being updated (caller-fetched, conflict-resolved).</param>
    /// <param name="remote">The freshly-fetched remote, used as the basis for
    ///     <paramref name="append"/> merges and as <c>expectedRevision</c>.</param>
    /// <param name="field">ADO reference name (e.g. <c>System.Title</c>).</param>
    /// <param name="effectiveValue">The value to write (already HTML-converted if applicable).</param>
    /// <param name="isHtml">Whether <paramref name="effectiveValue"/> is HTML — used by
    ///     <see cref="FieldAppender"/> when <paramref name="append"/> is true.</param>
    /// <param name="append">When true, append to the remote's existing field value.</param>
    public async Task<FieldUpdateOutcome> ExecuteAsync(
        WorkItem local,
        WorkItem remote,
        string field,
        string effectiveValue,
        bool isHtml,
        bool append,
        CancellationToken ct = default)
    {
        if (append)
        {
            remote.Fields.TryGetValue(field, out var existingValue);
            effectiveValue = FieldAppender.Append(existingValue, effectiveValue, asHtml: isHtml);
        }

        var changes = new[] { new FieldChange(field, null, effectiveValue) };
        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(adoService, local.Id, changes, remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            return new FieldUpdateOutcome.ConflictAfterRetry();
        }

        var warnings = new List<string>();

        try
        {
            await AutoPushNotesHelper.PushAndClearAsync(local.Id, pendingChangeStore, adoService);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"auto-push notes failed: {ex.Message}");
        }

        WorkItem updated;
        try
        {
            updated = await adoService.FetchAsync(local.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = local;
            warnings.Add($"cache may be stale after update — run 'twig sync' to resync ({ex.Message})");
        }

        if (promptStateWriter is not null)
        {
            try
            {
                await promptStateWriter.WritePromptStateAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"prompt-state write failed: {ex.Message}");
            }
        }

        return new FieldUpdateOutcome.Succeeded(updated, warnings);
    }
}
