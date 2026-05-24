using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Orchestrates a single-item discard: queries pending-change summary, branches
/// on the no-changes / phantom-dirty / has-changes cases, clears pending changes
/// and dirty flag when appropriate, and writes prompt state.
/// </summary>
/// <remarks>
/// <para>
/// Both <c>DiscardCommand</c> (single-item path) and <c>MutationTools.Discard</c>
/// route through this workflow. Adapter responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Resolve the work item from cache or ADO.</item>
///   <item>(CLI only) Reject seeds and prompt for confirmation.</item>
///   <item>Render the resulting <see cref="DiscardOutcome"/>.</item>
/// </list>
/// <para>
/// The <c>--all</c> CLI flow performs a batch clear with different store methods
/// (<c>ClearAllChangesAsync</c>, <c>ClearPhantomDirtyFlagsAsync</c>) and does
/// not route through this workflow.
/// </para>
/// </remarks>
public sealed class DiscardWorkflow(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>
    /// Discards pending changes for <paramref name="item"/>.
    /// </summary>
    /// <param name="item">The work item being discarded against. Caller is responsible
    /// for any seed guard and confirmation flow.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DiscardOutcome> ExecuteAsync(WorkItem item, CancellationToken ct = default)
    {
        var (notes, fieldEdits) = await pendingChangeStore.GetChangeSummaryAsync(item.Id, ct);

        if (!item.IsDirty && notes == 0 && fieldEdits == 0)
            return new DiscardOutcome.NoChanges(item);

        var warnings = new List<string>();

        if (item.IsDirty && notes == 0 && fieldEdits == 0)
        {
            await workItemRepo.ClearDirtyFlagAsync(item.Id, ct);
            await TryWritePromptStateAsync(warnings);
            return new DiscardOutcome.PhantomDirtyCleared(item, warnings);
        }

        await pendingChangeStore.ClearChangesAsync(item.Id, ct);
        await workItemRepo.ClearDirtyFlagAsync(item.Id, ct);
        await TryWritePromptStateAsync(warnings);

        return new DiscardOutcome.Discarded(item, notes, fieldEdits, warnings);
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
