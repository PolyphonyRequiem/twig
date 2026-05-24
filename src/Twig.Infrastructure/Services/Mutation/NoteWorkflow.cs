using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Orchestrates adding a comment/note to a work item: push to ADO with offline
/// fallback to local pending-changes staging, cache resync on push success,
/// prompt-state write.
/// </summary>
/// <remarks>
/// <para>
/// Both <c>NoteCommand</c> and <c>MutationTools.Note</c> route through this
/// workflow. Adapter responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Validate input (non-empty text, format flag).</item>
///   <item>Resolve the active or specified work item.</item>
///   <item>Resolve HTML conversion via <c>HtmlFieldFormatter.ResolveComment</c>.</item>
///   <item>(CLI only) launch editor when no inline text is supplied.</item>
///   <item>Render the resulting <see cref="NoteOutcome"/>.</item>
/// </list>
/// <para>
/// The workflow always succeeds — push failures fall through to local staging
/// (returns <see cref="NoteOutcome.Staged"/>) rather than propagating. Best-effort
/// side-effects (cache resync, prompt-state write) accumulate into
/// <c>Warnings</c> on the outcome.
/// </para>
/// </remarks>
public sealed class NoteWorkflow(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>
    /// Adds a comment to <paramref name="item"/>. Pushes to ADO when the item has
    /// an ADO identity; falls back to local staging when ADO is unreachable or
    /// when the item is a seed.
    /// </summary>
    /// <param name="item">The work item being commented on.</param>
    /// <param name="noteText">The comment body (already HTML-converted if applicable).</param>
    /// <param name="isHtml">True when <paramref name="noteText"/> is HTML.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NoteOutcome> ExecuteAsync(
        WorkItem item,
        string noteText,
        bool isHtml,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        if (item.IsSeed)
        {
            await StageLocallyAsync(item, noteText, isHtml, ct);
            await TryWritePromptStateAsync(warnings);
            return new NoteOutcome.Staged(item, WasOfflineFallback: false, FailureReason: null, warnings);
        }

        try
        {
            await adoService.AddCommentAsync(item.Id, noteText, ct);
            await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StageLocallyAsync(item, noteText, isHtml, ct);
            await TryWritePromptStateAsync(warnings);
            return new NoteOutcome.Staged(item, WasOfflineFallback: true, FailureReason: ex.Message, warnings);
        }

        WorkItem updated = item;
        try
        {
            updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Note pushed but cache may be stale — run 'twig sync' to resync ({ex.Message})");
            updated = item;
        }

        await TryWritePromptStateAsync(warnings);

        return new NoteOutcome.Pushed(updated, warnings);
    }

    private async Task StageLocallyAsync(WorkItem item, string noteText, bool isHtml, CancellationToken ct)
    {
        await pendingChangeStore.AddChangeAsync(
            item.Id,
            "note",
            fieldName: null,
            oldValue: null,
            newValue: noteText,
            ct);

        item.AddNote(new PendingNote(noteText, DateTimeOffset.UtcNow, IsHtml: isHtml));
        await workItemRepo.SaveAsync(item, ct);
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
            warnings.Add($"Failed to write prompt state: {ex.Message}");
        }
    }
}
