using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Pushes pending notes as ADO comments and clears them.
/// Shared by StateCommand and UpdateCommand.
/// </summary>
internal static class AutoPushNotesHelper
{
    /// <summary>
    /// Pushes any pending notes for the given work item as ADO comments,
    /// then clears the note-type pending changes.
    /// </summary>
    internal static async Task PushAndClearAsync(
        int workItemId,
        IPendingChangeStore pendingChangeStore,
        IAdoWorkItemService adoService)
    {
        var pendingChanges = await pendingChangeStore.GetChangesAsync(workItemId);
        var hasNotes = false;

        foreach (var change in pendingChanges)
        {
            if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase)
                && change.NewValue is not null)
            {
                await adoService.AddCommentAsync(workItemId, change.NewValue);
                hasNotes = true;
            }
        }

        if (hasNotes)
            await pendingChangeStore.ClearChangesByTypeAsync(workItemId, "note");
    }
}
