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

                // FR-9: Notes-only items skip conflict resolution — notes are additive
                // (ADO comments) and cannot conflict with field-level metadata drift.
                if (fieldChanges.Count > 0)
                {
                    var remote = await adoService.FetchAsync(item.Id, ct);
                    await ConflictRetryHelper.PatchWithRetryAsync(
                        adoService, item.Id, fieldChanges, remote.Revision, ct);
                }

                foreach (var note in notes)
                    await adoService.AddCommentAsync(item.Id, note, ct);

                // Post-push resync: clear local pending state and refresh from ADO.
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
