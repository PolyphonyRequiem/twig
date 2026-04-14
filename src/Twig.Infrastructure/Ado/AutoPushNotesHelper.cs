using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Pushes pending notes as ADO comments and clears them.
/// Shared by StateCommand and UpdateCommand.
/// </summary>
public static class AutoPushNotesHelper
{
    public static async Task PushAndClearAsync(
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
