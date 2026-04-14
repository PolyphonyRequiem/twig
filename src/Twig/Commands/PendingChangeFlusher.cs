using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado;

namespace Twig.Commands;

/// <summary>Structured result of a flush operation.</summary>
public sealed record FlushResult(
    int ItemsFlushed,
    int FieldChangesPushed,
    int NotesPushed,
    IReadOnlyList<FlushItemFailure> Failures);

/// <summary>Per-item failure detail for callers to render.</summary>
public sealed record FlushItemFailure(int ItemId, string Error);

/// <summary>
/// Pushes pending field changes and notes for a set of work items to Azure DevOps.
/// </summary>
/// <remarks>
/// Key behaviors:
/// <list type="bullet">
///   <item>FR-7: Continues past individual item failures, collecting them in <see cref="FlushResult.Failures"/>.</item>
///   <item>FR-9: Notes-only items bypass conflict resolution — notes are additive and cannot conflict.</item>
///   <item>After each successful push: ClearChangesAsync → FetchAsync → SaveAsync (cache resync).</item>
/// </list>
/// </remarks>
public sealed class PendingChangeFlusher(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null) : IPendingChangeFlusher
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Flushes pending changes for the specified item IDs.
    /// </summary>
    public async Task<FlushResult> FlushAsync(
        IReadOnlyList<int> itemIds,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var failures = new List<FlushItemFailure>();
        var totalFieldChanges = 0;
        var totalNotes = 0;
        var itemsFlushed = 0;

        foreach (var itemId in itemIds)
        {
            var item = await workItemRepo.GetByIdAsync(itemId, ct);
            if (item is null)
            {
                failures.Add(new FlushItemFailure(itemId, $"Work item #{itemId} not found in cache."));
                continue;
            }

            var pending = await pendingChangeStore.GetChangesAsync(item.Id, ct);
            if (pending.Count == 0)
                continue;

            try
            {
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

                // FR-9: Notes-only items skip conflict resolution.
                // Notes are additive (ADO comments) and cannot conflict with field-level
                // metadata drift. This prevents spurious conflict failures when the remote
                // has unrelated metadata changes.
                if (fieldChanges.Count > 0)
                {
                    var remote = await adoService.FetchAsync(item.Id, ct);

                    var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
                        item, remote, fmt, outputFormat, consoleInput, workItemRepo,
                        $"#{item.Id} synced from remote. Pending changes discarded.",
                        onAcceptRemote: () => pendingChangeStore.ClearChangesAsync(item.Id, ct));

                    if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
                    {
                        failures.Add(new FlushItemFailure(item.Id, "Unresolved conflict (JSON emitted)."));
                        continue;
                    }

                    if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
                        continue;

                    await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, fieldChanges, remote.Revision, ct);
                    totalFieldChanges += fieldChanges.Count;
                }

                foreach (var note in notes)
                    await adoService.AddCommentAsync(item.Id, note, ct);

                totalNotes += notes.Count;

                // Post-push resync: clear local pending state and refresh from ADO.
                await pendingChangeStore.ClearChangesAsync(item.Id, ct);
                var updated = await adoService.FetchAsync(item.Id, ct);
                await workItemRepo.SaveAsync(updated, ct);
                itemsFlushed++;
            }
            catch (Exception ex)
            {
                _stderr.WriteLine(fmt.FormatError($"Failed to save #{item.Id} {item.Title}: {ex.Message}"));
                failures.Add(new FlushItemFailure(item.Id, ex.Message));
            }
        }

        return new FlushResult(itemsFlushed, totalFieldChanges, totalNotes, failures);
    }

    /// <summary>
    /// Flushes pending changes for all dirty items.
    /// </summary>
    public async Task<FlushResult> FlushAllAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync(ct);
        return await FlushAsync(dirtyIds, outputFormat, ct);
    }
}
