using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Side-effect flusher that pushes locally-staged notes as ADO comments and clears them
/// from the pending change store. Not user-facing — invoked as a post-push side-effect by
/// <c>UpdateCommand</c>, <c>StateCommand</c>, and <c>EditCommand</c>.
/// </summary>
/// <remarks>
/// <para>
/// Notes accumulate in <c>pending_changes</c> when <c>NoteCommand</c> falls back to local
/// staging (offline fallback or seed items). When a subsequent <c>update</c>, <c>state</c>,
/// or <c>edit</c> command successfully pushes field changes to ADO, it calls
/// <see cref="PushAndClearAsync"/> to flush any residual staged notes as a side-effect.
/// </para>
/// <para><strong>Error-handling gradient across call sites:</strong></para>
/// <list type="bullet">
///   <item>
///     <term>EditCommand (most lenient)</term>
///     <description>
///       Wraps <c>PushAndClearAsync</c> in a try-catch — note push failure is swallowed with
///       a warning because field changes are already in ADO and must not trigger staging-fallback.
///       Post-push cache-resync is also wrapped in a separate try-catch — resync failure is
///       similarly non-fatal.
///     </description>
///   </item>
///   <item>
///     <term>StateCommand (middle)</term>
///     <description>
///       Calls <c>PushAndClearAsync</c> without a try-catch so note push failure propagates,
///       but catches subsequent cache-resync failure as non-fatal.
///     </description>
///   </item>
///   <item>
///     <term>UpdateCommand (strictest)</term>
///     <description>
///       Calls <c>PushAndClearAsync</c> without a try-catch and performs cache resync without
///       a try-catch — any failure propagates to the caller.
///     </description>
///   </item>
/// </list>
/// </remarks>
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
