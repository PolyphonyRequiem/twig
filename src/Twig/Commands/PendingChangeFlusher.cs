using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado;

namespace Twig.Commands;

/// <summary>Structured result of a flush operation.</summary>
/// <param name="ItemsFlushed">Number of items successfully pushed and resynced.</param>
/// <param name="FieldChangesPushed">Number of individual field changes pushed to ADO.</param>
/// <param name="NotesPushed">Number of notes pushed to ADO.</param>
/// <param name="Failures">Per-item failures encountered during the flush.</param>
/// <param name="FieldChangesStaged">
/// Number of field changes that were staged locally when the flush began. Lets callers
/// distinguish "nothing was pending" (staged == 0) from "something was pending but did not
/// get pushed" (staged &gt; 0 while pushed == 0) — see PolyphonyRequiem/twig#252.
/// </param>
/// <param name="NotesStaged">
/// Number of notes that were staged locally when the flush began. Same disambiguation as
/// <paramref name="FieldChangesStaged"/>.
/// </param>
public sealed record FlushResult(
    int ItemsFlushed,
    int FieldChangesPushed,
    int NotesPushed,
    IReadOnlyList<FlushItemFailure> Failures,
    int FieldChangesStaged = 0,
    int NotesStaged = 0);

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
///   <item>After each successful push: FetchAsync → resolve against the merge base →
///   ClearChangesAsync → SaveAsync (cache resync). The resync goes through
///   <c>ConflictResolutionFlow</c>, not around it (wayfinder 0004 slice 5).</item>
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

    /// <inheritdoc/>
    public async Task<FlushResult> FlushAsync(
        IReadOnlyList<int> itemIds,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var failures = new List<FlushItemFailure>();
        var totalFieldChanges = 0;
        var totalNotes = 0;
        var stagedFieldChanges = 0;
        var stagedNotes = 0;
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

                // Record what was staged before attempting any push, so callers can tell
                // "nothing was pending" apart from "was pending but never pushed" (#252).
                stagedFieldChanges += fieldChanges.Count;
                stagedNotes += notes.Count;

                // FR-9 / #251: notes are additive (ADO comments) and cannot conflict with
                // field-level metadata drift, so they are pushed FIRST — before the field
                // conflict flow can take an early exit. Previously a conflict resolved as
                // "accept remote" cleared every pending row for the item (notes included)
                // and an "abort" skipped the note push entirely, so a staged note could be
                // discarded, or left behind, without ever reaching ADO.
                if (notes.Count > 0)
                {
                    foreach (var note in notes)
                        await adoService.AddCommentAsync(item.Id, note, ct);

                    // Clear only the note rows: field changes may still be unresolved below.
                    await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);
                    totalNotes += notes.Count;
                }

                if (fieldChanges.Count > 0)
                {
                    var remote = await adoService.FetchAsync(item.Id, ct);

                    var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
                        item, remote, fmt, outputFormat, consoleInput, workItemRepo, pendingChangeStore,
                        $"#{item.Id} synced from remote. Pending changes discarded.",
                        onAcceptRemote: () => pendingChangeStore.ClearChangesAsync(item.Id, ct),
                        ct: ct);

                    if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
                    {
                        failures.Add(new FlushItemFailure(item.Id, "Unresolved conflict (JSON emitted)."));
                        continue;
                    }

                    if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
                        continue;

                    await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, fieldChanges, remote.Revision, ct);
                    totalFieldChanges += fieldChanges.Count;
                    await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "field", ct);
                }

                // Post-push resync. Wayfinder 0004 slice 5: this used to clear every remaining
                // pending row and then write the fetched remote with a raw SaveAsync — around
                // the resolver, five lines below a field-change path that goes through it.
                //
                // The guard is "is anything still staged that this flush did NOT push?", NOT
                // "is there a conflict?". Those are different questions and the difference was a
                // live defect: ConflictResolutionFlow returns Proceed whenever three-way merge
                // finds no conflict, and a change type outside field/state/set_field contributes
                // no merge base at all (MergeBase skips it), so it can never produce one. Keying
                // the clear off the resolver's outcome therefore left exactly the case this
                // comment claims to protect — an unrecognised staged row — still being destroyed.
                //
                // Rows this flush just pushed are excluded by value: a store re-read may still
                // report them (they were cleared a statement ago, and ADO echoes the same field
                // back normalised), and re-litigating a write that already succeeded would
                // re-prompt the user to resolve their own edit.
                var pushedFields = new HashSet<string>(
                    fieldChanges.Select(f => f.FieldName), StringComparer.OrdinalIgnoreCase);

                var unpushed = (await pendingChangeStore.GetChangesAsync(item.Id, ct))
                    .Where(c => !string.Equals(c.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                    .Where(c => c.FieldName is null || !pushedFields.Contains(c.FieldName))
                    .ToList();

                var updated = await adoService.FetchAsync(item.Id, ct);

                if (unpushed.Count > 0)
                {
                    var resyncOutcome = await ConflictResolutionFlow.ResolveAsync(
                        item, updated, fmt, outputFormat, consoleInput, workItemRepo, pendingChangeStore,
                        $"#{item.Id} synced from remote. Pending changes discarded.",
                        onAcceptRemote: () => pendingChangeStore.ClearChangesAsync(item.Id, ct),
                        ct: ct);

                    if (resyncOutcome == ConflictOutcome.ConflictJsonEmitted)
                    {
                        failures.Add(new FlushItemFailure(item.Id, "Unresolved conflict on post-push resync (JSON emitted)."));
                        continue;
                    }

                    // AcceptedRemote already cleared the rows and wrote the remote, by the user's
                    // explicit choice. Aborted leaves everything alone. Proceed with rows still
                    // staged means the staged edit STANDS, so overwriting the cache with remote
                    // would discard it as silently as the code this replaced. In every case the
                    // item keeps its pending state and is not counted as flushed.
                    continue;
                }

                await pendingChangeStore.ClearChangesAsync(item.Id, ct);
                await workItemRepo.SaveAsync(updated, ct);
                itemsFlushed++;
            }
            catch (Exception ex)
            {
                _stderr.WriteLine(fmt.FormatError($"Failed to save #{item.Id} {item.Title}: {ex.Message}"));
                failures.Add(new FlushItemFailure(item.Id, ex.Message));
            }
        }

        return new FlushResult(itemsFlushed, totalFieldChanges, totalNotes, failures, stagedFieldChanges, stagedNotes);
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
