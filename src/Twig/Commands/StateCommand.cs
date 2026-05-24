using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Services.Mutation;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig state &lt;name&gt;</c>: resolves a full or partial state name,
/// validates transition, pushes to ADO, auto-pushes pending notes,
/// and updates cache.
/// Routes through <see cref="SeedMutationProvider"/> for local-only seeds and
/// through <see cref="StateTransitionWorkflow"/> for published items.
/// </summary>
public sealed class StateCommand(
    CommandContext ctx,
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IConsoleInput consoleInput,
    SeedMutationProvider seedMutationProvider,
    StateTransitionWorkflow stateTransitionWorkflow)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;

    /// <summary>Change the state of the active work item by full or partial state name.</summary>
    public async Task<int> ExecuteAsync(string stateName, int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(stateName))
        {
            _stderr.WriteLine(fmt.FormatError("Usage: twig state <name> (e.g. Active, Closed, Res…)"));
            return 2;
        }

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        // Seed routing: local-only mutation, no process config or ADO interaction.
        if (item.IsSeed)
            return await ExecuteSeedAsync(item, stateName, fmt, ct);

        // Pre-flight validation: bail before fetch on bad input, invalid transitions, etc.
        var preflight = stateTransitionWorkflow.Validate(item, stateName);
        if (preflight is not null)
            return RenderOutcome(preflight, item, fmt, outputFormat);

        // Published flow — fetch remote, perform interactive conflict resolution,
        // then delegate the orchestration to the workflow.
        var remote = await adoService.FetchAsync(item.Id);

        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return 0;

        var outcome = await stateTransitionWorkflow.ExecuteAsync(item, stateName, remote.Revision, ct);
        return RenderOutcome(outcome, item, fmt, outputFormat);
    }

    private async Task<int> ExecuteSeedAsync(WorkItem item, string stateName, IOutputFormatter fmt, CancellationToken ct)
    {
        if (string.Equals(item.State, stateName, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(fmt.FormatInfo($"Already in state '{stateName}'."));
            return 0;
        }

        var change = new FieldChange("System.State", item.State, stateName);
        var result = await seedMutationProvider.ChangeStateAsync(item.Id, change, ct);
        if (!result.IsSuccess)
        {
            _stderr.WriteLine(fmt.FormatError(result.ErrorMessage ?? "Failed to change state."));
            return 1;
        }

        Console.WriteLine(fmt.FormatSuccess($"#{item.Id} {item.Title} → {stateName}"));
        return 0;
    }

    private int RenderOutcome(StateTransitionOutcome outcome, WorkItem item, IOutputFormatter fmt, string outputFormat)
    {
        switch (outcome)
        {
            case StateTransitionOutcome.InvalidStateName x:
                _stderr.WriteLine(fmt.FormatError(x.Error));
                return 1;

            case StateTransitionOutcome.ProcessConfigNotFound x:
                _stderr.WriteLine(fmt.FormatError($"No process configuration found for type '{x.Type}'."));
                return 1;

            case StateTransitionOutcome.AlreadyInState x:
                var alreadyMessage = x.ResolutionKind == ResolutionKind.Category
                    ? $"#{item.Id} already in '{x.ResolvedState}' (category '{x.Input}')"
                    : $"Already in state '{x.ResolvedState}'.";
                Console.WriteLine(fmt.FormatInfo(alreadyMessage));
                return 0;

            case StateTransitionOutcome.TransitionNotAllowed x:
                _stderr.WriteLine(fmt.FormatError($"Transition from '{x.FromState}' to '{x.ToState}' is not allowed."));
                return 1;

            case StateTransitionOutcome.ChainFailed x:
                var failureMessage = x.Path.Count > 1
                    ? $"#{x.ItemId} chain stopped at '{x.FinalState}'. Reached: {string.Join(" → ", x.Path)}. ADO: {x.AdoError}"
                    : $"#{x.ItemId} transition rejected. ADO: {x.AdoError}";
                _stderr.WriteLine(fmt.FormatError(failureMessage));
                if (x.CacheResyncWarning is not null)
                    _stderr.WriteLine($"warning: {x.CacheResyncWarning}");
                return 1;

            case StateTransitionOutcome.Succeeded x:
                foreach (var warning in x.Warnings)
                    _stderr.WriteLine($"warning: {warning}");

                var pathSuffix = x.Path.Count - 1 > 1
                    ? $": {string.Join(" → ", x.Path)} ({x.Path.Count - 1} transitions)"
                    : $" → {x.NewState}";
                var successMessage = x.ResolutionKind == ResolutionKind.Category
                    ? $"#{item.Id} {item.Title}{pathSuffix} (resolved category '{x.Input}' → '{x.NewState}')"
                    : $"#{item.Id} {item.Title}{pathSuffix}";
                Console.WriteLine(fmt.FormatSuccess(successMessage));

                var siblings = item.ParentId.HasValue
                    ? workItemRepo.GetChildrenAsync(item.ParentId.Value).GetAwaiter().GetResult()
                    : Array.Empty<WorkItem>();

                var hints = ctx.HintEngine.GetHints("state",
                    item: item,
                    outputFormat: outputFormat,
                    newStateName: x.NewState,
                    siblings: siblings);
                foreach (var hint in hints)
                {
                    var formatted = fmt.FormatHint(hint);
                    if (!string.IsNullOrEmpty(formatted))
                        Console.WriteLine(formatted);
                }
                return 0;

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled StateTransitionOutcome: {outcome.GetType().Name}");
        }
    }
}