using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig save</c>: pushes pending field changes and notes to ADO,
/// clears pending changes, and marks the item as clean.
/// Supports scoped save: active work tree (default), single item, or all dirty items.
/// </summary>
public sealed class SaveCommand(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IContextStore contextStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Push pending changes to Azure DevOps.</summary>
    /// <param name="targetId">When set, save only this single item.</param>
    /// <param name="all">When true, save all dirty items (legacy behavior).</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="skipPromptWrite">When true, suppresses the prompt state write. Used by
    /// <see cref="FlowDoneCommand"/> which performs its own write after state transition.</param>
    public async Task<int> ExecuteAsync(
        int? targetId = null,
        bool all = false,
        string outputFormat = "human",
        bool skipPromptWrite = false)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        _ = hintEngine; // No registered hints for save

        // Determine which items to save based on scoping parameters
        IReadOnlyList<int> itemsToSave;

        if (targetId.HasValue)
        {
            // Single item mode: save only the specified item
            itemsToSave = [targetId.Value];
        }
        else if (all)
        {
            // All mode: save all dirty items (original behavior)
            itemsToSave = await pendingChangeStore.GetDirtyItemIdsAsync();
        }
        else
        {
            // Active work tree mode: active item + dirty children
            var activeId = await contextStore.GetActiveWorkItemIdAsync();
            if (!activeId.HasValue)
            {
                Console.Error.WriteLine(fmt.FormatError("No active work item. Use 'twig save --all' or 'twig save <id>'."));
                return 1;
            }

            var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync();
            if (dirtyIds.Count == 0)
            {
                Console.WriteLine(fmt.FormatInfo("Nothing to save."));
                return 0;
            }

            var dirtySet = new HashSet<int>(dirtyIds);
            var workTreeIds = new List<int>();

            // Include active item if dirty
            if (dirtySet.Contains(activeId.Value))
                workTreeIds.Add(activeId.Value);

            // Include dirty children of the active item
            var children = await workItemRepo.GetChildrenAsync(activeId.Value);
            foreach (var child in children)
            {
                if (dirtySet.Contains(child.Id))
                    workTreeIds.Add(child.Id);
            }

            itemsToSave = workTreeIds;
        }

        if (itemsToSave.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("Nothing to save."));
            return 0;
        }

        var hadErrors = false;
        var anySaved = false;

        foreach (var itemId in itemsToSave)
        {
            var item = await workItemRepo.GetByIdAsync(itemId);
            if (item is null)
            {
                Console.Error.WriteLine(fmt.FormatError($"Work item #{itemId} not found in cache. Skipping."));
                hadErrors = true;
                continue;
            }

            var pending = await pendingChangeStore.GetChangesAsync(item.Id);
            if (pending.Count == 0)
                continue;

            // Pull latest revision
            var remote = await adoService.FetchAsync(item.Id);

            // FM-006: Conflict resolution
            var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
                item, remote, fmt, outputFormat, consoleInput, workItemRepo,
                $"#{item.Id} synced from remote. Pending changes discarded.",
                onAcceptRemote: () => pendingChangeStore.ClearChangesAsync(item.Id));
            if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            {
                hadErrors = true;
                continue;
            }
            if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
                continue;

            // Collect field changes and notes
            var fieldChanges = new List<FieldChange>();
            var notes = new List<string>();

            foreach (var change in pending)
            {
                if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                {
                    if (change.NewValue is not null)
                        notes.Add(change.NewValue);
                }
                else if (change.FieldName is not null)
                {
                    fieldChanges.Add(new FieldChange(change.FieldName, change.OldValue, change.NewValue));
                }
            }

            // Push field changes
            if (fieldChanges.Count > 0)
            {
                // Return value (new revision) discarded; cache is refreshed via FetchAsync below
                await adoService.PatchAsync(item.Id, fieldChanges, remote.Revision);
                Console.WriteLine(fmt.FormatSuccess($"Pushed {fieldChanges.Count} field change(s) for #{item.Id}."));
            }

            // Push notes as comments
            foreach (var note in notes)
            {
                await adoService.AddCommentAsync(item.Id, note);
            }

            if (notes.Count > 0)
                Console.WriteLine(fmt.FormatSuccess($"Pushed {notes.Count} note(s) for #{item.Id}."));

            // Clear pending and refresh cache
            await pendingChangeStore.ClearChangesAsync(item.Id);
            var updated = await adoService.FetchAsync(item.Id);
            await workItemRepo.SaveAsync(updated);
            anySaved = true;

            Console.WriteLine(fmt.FormatSuccess($"#{item.Id} saved and synced."));
        }

        if (anySaved && !skipPromptWrite)
            promptStateWriter?.WritePromptState();

        return hadErrors ? 1 : 0;
    }

}
