using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig flow-start &lt;idOrPattern&gt;</c>: resolves a work item, sets context,
/// transitions Proposed → InProgress, assigns to self, and creates/checks out a git branch.
/// When called with no argument, shows an interactive picker of unstarted sprint items.
/// </summary>
public sealed class FlowStartCommand(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    ProtectedCacheWriter protectedCacheWriter,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    RenderingPipelineFactory? pipelineFactory = null,
    IGitService? gitService = null,
    IIterationService? iterationService = null,
    IPromptStateWriter? promptStateWriter = null,
    INavigationHistoryStore? historyStore = null,
    ContextChangeService? contextChangeService = null,
    ParentStatePropagationService? parentPropagationService = null)
{
    /// <summary>Begin working on a work item: set context, transition state, assign, create branch.</summary>
    public async Task<int> ExecuteAsync(
        string? idOrPattern,
        bool noBranch = false,
        bool noState = false,
        bool noAssign = false,
        bool take = false,
        bool force = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        // No-argument path: interactive picker of unstarted sprint items
        if (string.IsNullOrWhiteSpace(idOrPattern))
        {
            if (iterationService is null)
            {
                Console.Error.WriteLine(fmt.FormatError("Usage: twig flow-start <id or pattern>"));
                return 2;
            }

            var pickerResult = await ShowPickerAsync(fmt, renderer, outputFormat);
            if (pickerResult is null)
                return 1;

            idOrPattern = pickerResult.Value.Id.ToString();
        }

        // 1. Resolve item via ActiveItemResolver (cache → auto-fetch)
        Domain.Aggregates.WorkItem? item = null;

        if (int.TryParse(idOrPattern, out var id))
        {
            var resolved = await activeItemResolver.ResolveByIdAsync(id);
            if (!resolved.TryGetWorkItem(out item, out var errId, out var errReason))
            {
                Console.Error.WriteLine(fmt.FormatError(errId is not null
                    ? $"Work item #{errId} could not be fetched: {errReason}"
                    : $"Work item #{id} not found."));
                return 1;
            }
            if (resolved is ActiveItemResult.FetchedFromAdo)
            {
                Console.WriteLine(fmt.FormatInfo($"Fetching work item {id} from ADO..."));
            }
        }
        else
        {
            var cached = await workItemRepo.FindByPatternAsync(idOrPattern);
            if (cached.Count == 1)
            {
                item = cached[0];
            }
            else if (cached.Count > 1)
            {
                var matches = cached.Select(c => (c.Id, $"{c.Title} [{c.State}]")).ToList();

                if (renderer is not null)
                {
                    var selected = await renderer.PromptDisambiguationAsync(matches, CancellationToken.None);
                    if (selected is not null)
                    {
                        item = cached.FirstOrDefault(c => c.Id == selected.Value.Id);
                        if (item is null) return 1;
                    }
                    else
                    {
                        return 1;
                    }
                }
                else
                {
                    Console.Error.WriteLine(fmt.FormatDisambiguation(matches));
                    return 1;
                }
            }
            else
            {
                Console.Error.WriteLine(fmt.FormatError($"No cached items match '{idOrPattern}'."));
                return 1;
            }
        }

        // 2. Fetch latest from ADO → conflict check
        var remote = await adoService.FetchAsync(item.Id);
        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} synced from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote)
        {
            // Re-read from cache after accepting remote
            item = (await workItemRepo.GetByIdAsync(item.Id))!;
        }

        // Use remote revision for subsequent patches
        var currentRevision = remote.Revision;

        // 3. Pre-flight: check for uncommitted git changes BEFORE any context/ADO mutations
        bool isInWorkTree = false;
        if (!noBranch && gitService is not null)
        {
            try
            {
                isInWorkTree = await gitService.IsInsideWorkTreeAsync();
                if (isInWorkTree && !force)
                {
                    var hasUncommitted = await gitService.HasUncommittedChangesAsync();
                    if (hasUncommitted)
                    {
                        Console.Error.WriteLine(fmt.FormatError(
                            "Uncommitted changes detected. Use --force to proceed or commit/stash first."));
                        return 1;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // DD-007: Git operations are skipped, not errored
            }
        }

        // 4. Set active context
        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        if (historyStore is not null)
            await historyStore.RecordVisitAsync(item.Id, ct);

        // 5. State transition: if Proposed → InProgress
        string? newState = null;
        string originalState = item.State;
        if (!noState)
        {
            var processConfig = processConfigProvider.GetConfiguration();
            if (processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            {
                var category = StateCategoryResolver.Resolve(item.State, typeConfig.StateEntries);
                if (category == StateCategory.Proposed)
                {
                    var resolveResult = StateResolver.ResolveByCategory(StateCategory.InProgress, typeConfig.StateEntries);
                    if (resolveResult.IsSuccess)
                    {
                        newState = resolveResult.Value;
                        var changes = new[] { new FieldChange("System.State", item.State, newState) };
                        currentRevision = await adoService.PatchAsync(item.Id, changes, currentRevision);
                        item.ChangeState(newState);
                        item.MarkSynced(currentRevision);
                        await protectedCacheWriter.SaveProtectedAsync(item);
                    }
                }
            }
        }

        // Parent propagation: child just moved to InProgress — activate parent if still Proposed.
        // Best-effort: the service never throws; failures do not affect the child command.
        if (newState is not null && parentPropagationService is not null)
            _ = await parentPropagationService.TryPropagateToParentAsync(item, StateCategory.InProgress, ct);

        // 6. Assignment logic
        string? assignedDisplayName = null;
        if (!noAssign)
        {
            var shouldAssign = string.IsNullOrWhiteSpace(item.AssignedTo) || take;

            if (shouldAssign)
            {
                var displayName = config.User.DisplayName;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    // DisplayName not configured — skip assignment silently
                }
                else
                {
                    assignedDisplayName = displayName;
                    var assignChanges = new[] { new FieldChange("System.AssignedTo", item.AssignedTo, displayName) };
                    currentRevision = await adoService.PatchAsync(item.Id, assignChanges, currentRevision);
                    // Re-fetch to update cache with new assignee (AssignedTo is init-only)
                    var refreshed = await adoService.FetchAsync(item.Id);
                    await protectedCacheWriter.SaveProtectedAsync(refreshed);
                    item = refreshed;
                }
            }
        }

        // 7. Git branch creation (if git available and not --no-branch)
        string? branchName = null;
        bool branchCreated = false;
        if (!noBranch && gitService is not null && isInWorkTree)
        {
            try
            {
                branchName = BranchNamingService.Generate(
                    item, config.Git.BranchTemplate, config.Git.TypeMap);

                var branchExists = await gitService.BranchExistsAsync(branchName);
                if (branchExists)
                {
                    await gitService.CheckoutAsync(branchName);
                }
                else
                {
                    await gitService.CreateBranchAsync(branchName);
                    await gitService.CheckoutAsync(branchName);
                    branchCreated = true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // DD-007: Git operations are skipped, not errored
            }
        }

        // 8. Print summary
        var actionStrings = new List<string>();
        actionStrings.Add($"Context set to #{item.Id}");
        if (newState is not null)
            actionStrings.Add($"State → {newState}");
        if (branchName is not null)
            actionStrings.Add($"Branch: {branchName}");

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary(
                item.Id, item.Title, item.Type.Value, originalState, newState,
                assignedDisplayName, branchName, branchCreated));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            // Minimal: branch name only (for script capture)
            Console.WriteLine(branchName ?? "");
        }
        else
        {
            if (renderer is not null)
            {
                await renderer.RenderFlowSummaryAsync(item, originalState, newState, branchName, ct);
            }
            else if (fmt is HumanOutputFormatter humanFmt)
            {
                Console.WriteLine(humanFmt.FormatFlowSummary(item.Id, item.Title, originalState, newState, branchName));
            }
            else
            {
                Console.WriteLine(fmt.FormatSuccess($"Flow started for #{item.Id} — {item.Title}"));
                foreach (var action in actionStrings)
                    Console.WriteLine(fmt.FormatInfo($"  {action}"));
            }
        }

        var hints = hintEngine.GetHints("flow-start", item: item, outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        // Extend working set around the target item (fire-and-forget — never fails the command).
        // Runs after output so user sees success immediately.
        if (contextChangeService is not null)
            await contextChangeService.ExtendWorkingSetAsync(item.Id, ct);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    /// <summary>
    /// Shows an interactive picker of unstarted sprint items (Proposed state category).
    /// Returns the selected item's (Id, Title) or null if cancelled/no items.
    /// </summary>
    private async Task<(int Id, string Title)?> ShowPickerAsync(
        IOutputFormatter fmt, IAsyncRenderer? renderer, string outputFormat)
    {
        var iteration = await iterationService!.GetCurrentIterationAsync();
        var displayName = config.User.DisplayName ?? "";

        var items = string.IsNullOrWhiteSpace(displayName)
            ? await workItemRepo.GetByIterationAsync(iteration)
            : await workItemRepo.GetByIterationAndAssigneeAsync(iteration, displayName);

        // Filter to Proposed state category.
        // Items whose type is not in the local process config are excluded because their
        // state category cannot be resolved — this is intentional (DD-007 style graceful skip).
        var processConfig = processConfigProvider.GetConfiguration();
        var proposed = new List<Domain.Aggregates.WorkItem>();
        foreach (var wi in items)
        {
            if (processConfig.TypeConfigs.TryGetValue(wi.Type, out var typeConfig))
            {
                var category = StateCategoryResolver.Resolve(wi.State, typeConfig.StateEntries);
                if (category == StateCategory.Proposed)
                    proposed.Add(wi);
            }
        }

        if (proposed.Count == 0)
        {
            Console.Error.WriteLine(fmt.FormatError("No unstarted items in current sprint."));
            return null;
        }

        var matches = proposed.Select(p => (p.Id, $"{p.Title} [{p.State}]")).ToList();

        if (renderer is not null)
        {
            // TTY: interactive picker
            return await renderer.PromptDisambiguationAsync(matches, CancellationToken.None);
        }
        else
        {
            // Non-TTY: print list and error
            Console.Error.WriteLine(fmt.FormatError("Interactive picker not available (non-TTY). Available items:"));
            Console.Error.WriteLine(fmt.FormatDisambiguation(matches));
            return null;
        }
    }

    private static string FormatJsonSummary(
        int id, string title, string type, string originalState, string? newState,
        string? assignedTo, string? branchName, bool branchCreated)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "flow start");
        writer.WriteNumber("itemId", id);
        writer.WriteString("title", title);
        writer.WriteString("type", type);

        // Structured actions object matching scenario doc contract
        writer.WriteStartObject("actions");
        writer.WriteBoolean("contextSet", true);

        if (newState is not null)
        {
            writer.WriteStartObject("stateChanged");
            writer.WriteString("from", originalState);
            writer.WriteString("to", newState);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("stateChanged");
        }

        if (assignedTo is not null)
        {
            writer.WriteStartObject("assigned");
            writer.WriteString("to", assignedTo);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("assigned");
        }

        if (branchName is not null)
        {
            writer.WriteStartObject("branch");
            writer.WriteString("name", branchName);
            writer.WriteBoolean("created", branchCreated);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("branch");
        }

        writer.WriteEndObject(); // actions

        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
