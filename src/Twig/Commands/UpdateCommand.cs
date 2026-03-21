using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig update &lt;field&gt; &lt;value&gt;</c>: pulls latest from ADO,
/// conflict-resolves, applies change, pushes, auto-pushes notes, clears pending, updates cache.
/// </summary>
public sealed class UpdateCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Update a field on the active work item and push to ADO.</summary>
    public async Task<int> ExecuteAsync(string field, string value, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        _ = hintEngine; // No dedicated hint for update

        if (string.IsNullOrWhiteSpace(field))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig update <field> <value>"));
            return 2;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var local, out var errorId, out var errorReason))
        {
            Console.Error.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // Pull latest from ADO
        var remote = await adoService.FetchAsync(local.Id);

        // FM-006: Conflict resolution with l/r/a prompt
        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            local, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{local.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return 0;

        // Apply the update
        var changes = new[] { new FieldChange(field, null, value) };
        var newRevision = await adoService.PatchAsync(local.Id, changes, remote.Revision);

        // Auto-push pending notes (preserve field changes)
        await AutoPushNotesHelper.PushAndClearAsync(local.Id, pendingChangeStore, adoService);

        // Refresh cache with the latest from ADO
        var updated = await adoService.FetchAsync(local.Id);
        await workItemRepo.SaveAsync(updated);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        Console.WriteLine(fmt.FormatSuccess($"#{local.Id} updated: {field} = '{value}'"));

        return 0;
    }

}
