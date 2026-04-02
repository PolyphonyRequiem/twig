using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig state &lt;name&gt;</c>: resolves a full or partial state name,
/// validates transition, prompts if backward/cut, pushes to ADO, auto-pushes pending notes,
/// and updates cache.
/// </summary>
public sealed class StateCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Change the state of the active work item by full or partial state name.</summary>
    public async Task<int> ExecuteAsync(string stateName, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(stateName))
        {
            _stderr.WriteLine(fmt.FormatError("Usage: twig state <name> (e.g. Active, Closed, Res…)"));
            return 2;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        var processConfig = processConfigProvider.GetConfiguration();

        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
        {
            _stderr.WriteLine(fmt.FormatError($"No process configuration found for type '{item.Type}'."));
            return 1;
        }

        var resolveResult = StateResolver.ResolveByName(stateName, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess)
        {
            _stderr.WriteLine(fmt.FormatError(resolveResult.Error));
            return 1;
        }

        var newState = resolveResult.Value;
        if (string.Equals(item.State, newState, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(fmt.FormatInfo($"Already in state '{newState}'."));
            return 0;
        }

        var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState);

        if (!transition.IsAllowed)
        {
            _stderr.WriteLine(fmt.FormatError($"Transition from '{item.State}' to '{newState}' is not allowed."));
            return 1;
        }

        if (transition.RequiresConfirmation)
        {
            var kind = transition.Kind == TransitionKind.Cut ? "REMOVE" : "move backward";
            Console.Write($"This will {kind} #{item.Id} from '{item.State}' to '{newState}'. Continue? [y/N] ");
            var response = consoleInput.ReadLine()?.Trim();
            if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(fmt.FormatInfo("Cancelled."));
                return 0;
            }
        }

        var remote = await adoService.FetchAsync(item.Id);

        // FM-006: Conflict detection before state change
        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return 0;

        var changes = new[] { new FieldChange("System.State", item.State, newState) };
        await ConflictRetryHelper.PatchWithRetryAsync(
            adoService, item.Id, changes, remote.Revision, ct);

        // Auto-push pending notes (preserve field changes)
        await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);

        // Resync: re-fetch server-computed fields and update cache.
        // Failure is non-fatal — the ADO transition already succeeded.
        try
        {
            var updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine($"warning: State changed to '{newState}' but cache may be stale — run 'twig sync' to resync ({ex.Message})");
        }

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        var siblings = item.ParentId.HasValue
            ? await workItemRepo.GetChildrenAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        Console.WriteLine(fmt.FormatSuccess($"#{item.Id} {item.Title} → {newState}"));

        var hints = hintEngine.GetHints("state",
            item: item,
            outputFormat: outputFormat,
            newStateName: newState,
            siblings: siblings);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

}
