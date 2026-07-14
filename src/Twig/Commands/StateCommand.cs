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
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig state &lt;name&gt;</c>: resolves a full or partial state name,
/// validates transition, pushes to ADO, auto-pushes pending notes,
/// and updates cache.
/// Routes through <see cref="SeedMutationProvider"/> for local-only seeds and
/// through <see cref="StateTransitionWorkflow"/> for published items.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: success/info/hint output is built as a <see cref="RenderTree.RenderTree"/>
/// per output format. <see cref="OutputFormatterFactory"/> is retained only for
/// stderr error formatting (matching the SetCommand and NoteCommand migrations).
/// </remarks>
public sealed class StateCommand(
    CommandContext ctx,
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IConsoleInput consoleInput,
    SeedMutationProvider seedMutationProvider,
    StateTransitionWorkflow stateTransitionWorkflow,
    RendererFactory? rendererFactory = null)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

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

        if (item.IsSeed)
            return await ExecuteSeedAsync(item, stateName, fmt, outputFormat, ct);

        var preflight = stateTransitionWorkflow.Validate(item, stateName);
        if (preflight is not null)
            return RenderOutcome(preflight, item, fmt, outputFormat);

        var remote = await adoService.FetchAsync(item.Id);

        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return 0;

        var outcome = await stateTransitionWorkflow.ExecuteAsync(remote, stateName, remote.Revision, ct);
        return RenderOutcome(outcome, item, fmt, outputFormat);
    }

    private async Task<int> ExecuteSeedAsync(WorkItem item, string stateName, IOutputFormatter fmt, string outputFormat, CancellationToken ct)
    {
        if (string.Equals(item.State, stateName, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Already in state '{stateName}'.";
            RenderAlreadyInState(item.Id, item.State, stateName, isCategoryResolution: false, message, outputFormat);
            return 0;
        }

        var change = new FieldChange("System.State", item.State, stateName);
        var result = await seedMutationProvider.ChangeStateAsync(item.Id, change, ct);
        if (!result.IsSuccess)
        {
            _stderr.WriteLine(fmt.FormatError(result.ErrorMessage ?? "Failed to change state."));
            return 1;
        }

        var successMessage = $"#{item.Id} {item.Title} → {stateName}";
        RenderStateChanged(
            item,
            fromState: item.State,
            toState: stateName,
            requestedState: stateName,
            transitionCount: 1,
            isCategoryResolution: false,
            message: successMessage,
            hints: Array.Empty<string>(),
            outputFormat: outputFormat);
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
                RenderAlreadyInState(
                    item.Id,
                    state: x.ResolvedState,
                    requestedState: x.Input,
                    isCategoryResolution: x.ResolutionKind == ResolutionKind.Category,
                    message: alreadyMessage,
                    outputFormat: outputFormat);
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

                var transitionCount = Math.Max(0, x.Path.Count - 1);
                var pathSuffix = transitionCount > 1
                    ? $": {string.Join(" → ", x.Path)} ({transitionCount} transitions)"
                    : $" → {x.NewState}";
                var successMessage = x.ResolutionKind == ResolutionKind.Category
                    ? $"#{item.Id} {item.Title}{pathSuffix} (resolved category '{x.Input}' → '{x.NewState}')"
                    : $"#{item.Id} {item.Title}{pathSuffix}";

                var siblings = item.ParentId.HasValue
                    ? workItemRepo.GetChildrenAsync(item.ParentId.Value).GetAwaiter().GetResult()
                    : Array.Empty<WorkItem>();

                var hints = ctx.HintEngine.GetHints("state",
                    item: item,
                    outputFormat: outputFormat,
                    newStateName: x.NewState,
                    siblings: siblings);

                RenderStateChanged(
                    item,
                    fromState: x.Path.Count > 0 ? x.Path[0] : item.State,
                    toState: x.NewState,
                    requestedState: x.Input,
                    transitionCount: transitionCount,
                    isCategoryResolution: x.ResolutionKind == ResolutionKind.Category,
                    message: successMessage,
                    hints: hints,
                    outputFormat: outputFormat);
                return 0;

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled StateTransitionOutcome: {outcome.GetType().Name}");
        }
    }

    private void RenderStateChanged(
        WorkItem item,
        string fromState,
        string toState,
        string requestedState,
        int transitionCount,
        bool isCategoryResolution,
        string message,
        IReadOnlyList<string> hints,
        string outputFormat)
    {
        var tree = BuildStateChangedTree(item, fromState, toState, requestedState, transitionCount, isCategoryResolution, message, hints, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderAlreadyInState(int itemId, string state, string requestedState, bool isCategoryResolution, string message, string outputFormat)
    {
        var tree = BuildAlreadyInStateTree(itemId, state, requestedState, isCategoryResolution, message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildStateChangedTree(
        WorkItem item,
        string fromState,
        string toState,
        string requestedState,
        int transitionCount,
        bool isCategoryResolution,
        string message,
        IReadOnlyList<string> hints,
        string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";
        var nodes = new List<RenderNode>(capacity: 1 + (isMachine ? 0 : hints.Count));

        nodes.Add(lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildStateChangedRecord(item, fromState, toState, requestedState, transitionCount, isCategoryResolution, message),
            _ => new RenderNode.Text(message, Severity.Success),
        });

        // Hints only on human output. Adding Hint nodes for JSON formats would
        // turn a single-root Record document into an array with a `kind` discriminator.
        if (!isMachine)
        {
            foreach (var hint in hints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                    nodes.Add(new RenderNode.Hint(hint));
            }
        }

        return new RenderTree.RenderTree(nodes);
    }

    private static RenderTree.RenderTree BuildAlreadyInStateTree(int itemId, string state, string requestedState, bool isCategoryResolution, string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildAlreadyInStateRecord(itemId, state, requestedState, isCategoryResolution, message),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildStateChangedRecord(
        WorkItem item,
        string fromState,
        string toState,
        string requestedState,
        int transitionCount,
        bool isCategoryResolution,
        string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(item.Id.ToString(), new RenderValue.Integer(item.Id)),
            ["title"] = new RenderCell(item.Title, new RenderValue.String(item.Title)),
            ["fromState"] = new RenderCell(fromState, new RenderValue.String(fromState)),
            ["toState"] = new RenderCell(toState, new RenderValue.String(toState)),
            ["requestedState"] = new RenderCell(requestedState, new RenderValue.String(requestedState)),
            ["transitionCount"] = new RenderCell(transitionCount.ToString(), new RenderValue.Integer(transitionCount)),
            ["isCategoryResolution"] = new RenderCell(isCategoryResolution ? "true" : "false", new RenderValue.Boolean(isCategoryResolution)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("stateChanged", fields);
    }

    private static RenderNode BuildAlreadyInStateRecord(int itemId, string state, string requestedState, bool isCategoryResolution, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(itemId.ToString(), new RenderValue.Integer(itemId)),
            ["state"] = new RenderCell(state, new RenderValue.String(state)),
            ["requestedState"] = new RenderCell(requestedState, new RenderValue.String(requestedState)),
            ["isCategoryResolution"] = new RenderCell(isCategoryResolution ? "true" : "false", new RenderValue.Boolean(isCategoryResolution)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("alreadyInState", fields);
    }
}
