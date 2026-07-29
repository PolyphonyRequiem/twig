using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;

namespace Twig.Mcp.Services;

/// <summary>
/// Headless pending-change flusher for MCP.
/// Unlike the CLI's <c>PendingChangeFlusher</c>, this variant:
/// <list type="bullet">
///   <item>Has no <c>IConsoleInput</c> dependency — auto-accepts remote on conflict</item>
///   <item>Does not use <c>OutputFormatterFactory</c> — MCP tools handle their own output</item>
///   <item>Does not implement <c>IPendingChangeFlusher</c> (that interface has CLI-specific parameters)</item>
/// </list>
/// On conflict, auto-accepts the remote revision and retries via <see cref="ConflictRetryHelper"/>.
/// The post-push resync never clears rows this flush did not push (#329): if any remain staged,
/// the item keeps its pending state, is not counted as flushed, and is reported as a failure.
/// Returns <see cref="McpFlushSummary"/> for MCP tool response formatting (FR-8).
/// </summary>
public sealed class McpPendingChangeFlusher(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore)
{
    /// <summary>
    /// Flushes all pending changes to Azure DevOps.
    /// Continues past individual item failures (FR-7), collecting them in the summary.
    /// Notes-only items bypass conflict resolution (FR-9).
    /// </summary>
    public async Task<McpFlushSummary> FlushAllAsync(CancellationToken ct = default)
    {
        var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync(ct);
        var failures = new List<McpFlushItemFailure>();
        var flushed = 0;

        foreach (var itemId in dirtyIds)
        {
            var item = await workItemRepo.GetByIdAsync(itemId, ct);
            if (item is null)
            {
                failures.Add(new McpFlushItemFailure
                {
                    WorkItemId = itemId,
                    Reason = $"Work item #{itemId} not found in cache.",
                });
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

                // FR-9 / #251: notes are additive (ADO comments) and cannot conflict with
                // field-level metadata drift, so they are pushed FIRST. Pushing them after
                // the field patch meant a patch failure threw past the note push, leaving
                // the note stranded behind an unrelated field error.
                if (notes.Count > 0)
                {
                    foreach (var note in notes)
                        await adoService.AddCommentAsync(item.Id, note, ct);

                    // Clear only the note rows: the field patch below may still fail.
                    await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);
                }

                if (fieldChanges.Count > 0)
                {
                    var remote = await adoService.FetchAsync(item.Id, ct);
                    await ConflictRetryHelper.PatchWithRetryAsync(
                        adoService, item.Id, fieldChanges, remote.Revision, ct);

                    // Clear the rows this flush pushed AT the push, not at the end of the
                    // loop, so the resync guard below sees only what is genuinely unpushed.
                    await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "field", ct);
                }

                // Post-push resync (#329, mirroring the CLI fix in #327). This used to clear
                // every remaining pending row unconditionally and overwrite the cache with
                // remote — destroying staged rows this flush never pushed.
                //
                // The guard is "is anything still staged that this flush did NOT push?", NOT
                // "is there a conflict?". A change type outside the field/state/set_field
                // vocabulary contributes no merge base at all, so it can never produce a
                // conflict; keying the clear off a conflict outcome would leave exactly the
                // case this comment claims to protect still being destroyed.
                //
                // Rows this flush just pushed are excluded BY VALUE: a store re-read may still
                // report them (they were cleared a statement ago, and ADO echoes the same field
                // back normalised), and re-litigating a write that already succeeded would
                // manufacture a failure on an edit that worked.
                //
                // This variant is headless and has no IConsoleInput, so it cannot prompt the way
                // the CLI does. It keeps the documented auto-accept-remote behaviour for genuine
                // field conflicts (ConflictRetryHelper, above) and simply declines to destroy
                // unpushed rows: they stay staged, the item is NOT counted as flushed, and the
                // caller is told via McpFlushItemFailure.
                var pushedFields = new HashSet<string>(
                    fieldChanges.Select(f => f.FieldName), StringComparer.OrdinalIgnoreCase);

                var unpushed = (await pendingChangeStore.GetChangesAsync(item.Id, ct))
                    .Where(c => !string.Equals(c.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                    .Where(c => c.FieldName is null || !pushedFields.Contains(c.FieldName))
                    .ToList();

                if (unpushed.Count > 0)
                {
                    failures.Add(new McpFlushItemFailure
                    {
                        WorkItemId = item.Id,
                        Reason =
                            $"#{item.Id} has {unpushed.Count} staged change(s) this flush could not push "
                            + "(unrecognised change type, or staged during the flush). They were left "
                            + "staged rather than discarded; the item was not resynced.",
                    });
                    continue;
                }

                await pendingChangeStore.ClearChangesAsync(item.Id, ct);
                var updated = await adoService.FetchAsync(item.Id, ct);
                await workItemRepo.SaveAsync(updated, ct);
                flushed++;
            }
            catch (Exception ex)
            {
                failures.Add(new McpFlushItemFailure
                {
                    WorkItemId = item.Id,
                    Reason = ex.Message,
                });
            }
        }

        return new McpFlushSummary
        {
            Flushed = flushed,
            Failed = failures.Count,
            Failures = failures,
        };
    }
}
