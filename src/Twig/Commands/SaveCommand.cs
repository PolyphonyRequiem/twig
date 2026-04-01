using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

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
    ActiveItemResolver activeItemResolver,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    /// <summary>Push pending changes to Azure DevOps.</summary>
    /// <param name="targetId">When set, save only this single item.</param>
    /// <param name="all">When true, save all dirty items (legacy behavior).</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="skipPromptWrite">When true, suppresses the prompt state write. Used by
    /// <see cref="FlowDoneCommand"/> which performs its own write after state transition.</param>
    public async Task<int> ExecuteAsync(
        int? targetId = null,
        bool all = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool skipPromptWrite = false,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        IReadOnlyList<int> itemsToSave;

        if (targetId.HasValue)
        {
            itemsToSave = [targetId.Value];
        }
        else if (all)
        {
            itemsToSave = await pendingChangeStore.GetDirtyItemIdsAsync();
        }
        else
        {
            // Active work tree mode: active item + dirty children
            var activeResult = await activeItemResolver.GetActiveItemAsync();
            if (!activeResult.TryGetWorkItem(out var activeItem, out var errorId, out var errorReason))
            {
                _stderr.WriteLine(fmt.FormatError(errorId is not null
                    ? $"Work item #{errorId} not found in cache."
                    : "No active work item. Use 'twig save --all' or 'twig save <id>'."));
                return 1;
            }

            var activeId = activeItem.Id;

            var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync();
            if (dirtyIds.Count == 0)
            {
                Console.WriteLine(fmt.FormatInfo("Nothing to save."));
                return 0;
            }

            var children = await workItemRepo.GetChildrenAsync(activeId);
            var childIds = new HashSet<int>(children.Select(c => c.Id));
            itemsToSave = dirtyIds.Where(id => id == activeId || childIds.Contains(id)).ToList();
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
                _stderr.WriteLine(fmt.FormatError($"Work item #{itemId} not found in cache. Skipping."));
                hadErrors = true;
                continue;
            }

            var pending = await pendingChangeStore.GetChangesAsync(item.Id);
            if (pending.Count == 0)
                continue;

            try
            {
                // Categorize pending changes before conflict resolution to detect notes-only items.
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

                // FR-9: When only notes are pending, bypass conflict resolution.
                // Notes are additive (ADO comments) and cannot conflict with field-level
                // metadata drift. This prevents spurious conflict failures when the remote
                // has unrelated metadata changes (e.g., iteration path updated by a teammate).
                if (fieldChanges.Count > 0)
                {
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

                    // Return value (new revision) discarded; cache is refreshed via FetchAsync below
                    await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, fieldChanges, remote.Revision, ct);
                    Console.WriteLine(fmt.FormatSuccess($"Pushed {fieldChanges.Count} field change(s) for #{item.Id}."));
                }

                foreach (var note in notes)
                {
                    await adoService.AddCommentAsync(item.Id, note);
                }

                if (notes.Count > 0)
                    Console.WriteLine(fmt.FormatSuccess($"Pushed {notes.Count} note(s) for #{item.Id}."));

                await pendingChangeStore.ClearChangesAsync(item.Id);
                var updated = await adoService.FetchAsync(item.Id);
                await workItemRepo.SaveAsync(updated);
                anySaved = true;

                Console.WriteLine(fmt.FormatSuccess($"#{item.Id} saved and synced."));
            }
            catch (Exception ex)
            {
                _stderr.WriteLine(fmt.FormatError($"Failed to save #{item.Id}: {ex.Message}"));
                hadErrors = true;
            }
        }

        if (anySaved && !skipPromptWrite && promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return hadErrors ? 1 : 0;
    }

}
