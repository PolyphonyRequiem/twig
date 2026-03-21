using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig flow-done</c>: saves active work tree, transitions to Resolved,
/// and offers PR creation if the branch is ahead of the default target.
/// </summary>
public sealed class FlowDoneCommand(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    SaveCommand saveCommand,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    ActiveItemResolver activeItemResolver,
    ProtectedCacheWriter protectedCacheWriter,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Mark a work item as done: save work tree, transition to Resolved, offer PR.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool noSave = false,
        bool noPr = false,
        string outputFormat = "human")
    {
        _ = hintEngine; // No registered hints for flow-done yet

        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Resolve target via ActiveItemResolver
        int targetId;
        bool isExplicitId = id.HasValue;
        Domain.Aggregates.WorkItem item;

        if (id.HasValue)
        {
            var resolved = await activeItemResolver.ResolveByIdAsync(id.Value);
            switch (resolved)
            {
                case ActiveItemResult.Found found:
                    item = found.WorkItem;
                    break;
                case ActiveItemResult.FetchedFromAdo fetched:
                    item = fetched.WorkItem;
                    break;
                case ActiveItemResult.Unreachable unreachable:
                    Console.Error.WriteLine(fmt.FormatError($"Work item #{unreachable.Id} could not be fetched: {unreachable.Reason}"));
                    return 1;
                default:
                    Console.Error.WriteLine(fmt.FormatError($"Work item #{id.Value} not found in cache."));
                    return 1;
            }
            targetId = item.Id;
        }
        else
        {
            var activeResult = await activeItemResolver.GetActiveItemAsync();
            switch (activeResult)
            {
                case ActiveItemResult.NoContext:
                    Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig flow-start <id>' first."));
                    return 1;
                case ActiveItemResult.Found found:
                    item = found.WorkItem;
                    break;
                case ActiveItemResult.FetchedFromAdo fetched:
                    item = fetched.WorkItem;
                    break;
                case ActiveItemResult.Unreachable unreachable:
                    Console.Error.WriteLine(fmt.FormatError($"Work item #{unreachable.Id} could not be fetched: {unreachable.Reason}"));
                    return 1;
                default:
                    Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig flow-start <id>' first."));
                    return 1;
            }
            targetId = item.Id;
        }

        // 2. Save work tree (if not --no-save)
        bool workTreeSaved = false;
        if (!noSave)
        {
            // Check if there are dirty items to save before calling SaveCommand
            var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync();
            bool hasDirtyItems;

            int saveResult;
            if (isExplicitId)
            {
                // Explicit ID: save single item only, do NOT change active context
                hasDirtyItems = dirtyIds.Any(d => d == targetId);
                saveResult = await saveCommand.ExecuteAsync(targetId: targetId, all: false, outputFormat: outputFormat, skipPromptWrite: true);
            }
            else
            {
                // No explicit ID: save active work tree — scope dirty check to active item + children
                var dirtySet = new HashSet<int>(dirtyIds);
                var children = await workItemRepo.GetChildrenAsync(targetId);
                hasDirtyItems = dirtySet.Contains(targetId) || children.Any(c => dirtySet.Contains(c.Id));
                saveResult = await saveCommand.ExecuteAsync(targetId: null, all: false, outputFormat: outputFormat, skipPromptWrite: true);
            }

            if (saveResult != 0)
                return saveResult;

            workTreeSaved = hasDirtyItems;
        }

        // 3. Transition state: InProgress → Resolved (or Completed fallback)
        string? newState = null;
        string originalState = item.State;
        var processConfig = processConfigProvider.GetConfiguration();
        if (processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
        {
            var category = StateCategoryResolver.Resolve(item.State, typeConfig.StateEntries);

            // Skip transition if already Resolved or Completed
            if (category is not (StateCategory.Resolved or StateCategory.Completed))
            {
                // Try Resolved first ('s'), fall back to Completed ('d')
                var resolveResult = StateResolver.ResolveByCategory(StateCategory.Resolved, typeConfig.StateEntries);
                if (!resolveResult.IsSuccess)
                    resolveResult = StateResolver.ResolveByCategory(StateCategory.Completed, typeConfig.StateEntries);

                if (resolveResult.IsSuccess)
                {
                    newState = resolveResult.Value;
                    // Re-fetch to get latest revision after save
                    var remote = await adoService.FetchAsync(item.Id);
                    var changes = new[] { new FieldChange("System.State", item.State, newState) };
                    var newRevision = await adoService.PatchAsync(item.Id, changes, remote.Revision);
                    item.ChangeState(newState);
                    item.ApplyCommands();
                    item.MarkSynced(newRevision);
                    await protectedCacheWriter.SaveProtectedAsync(item);
                }
            }
        }

        // 4. Offer PR creation (if git available and not --no-pr)
        PullRequestInfo? createdPr = null;
        if (!noPr && gitService is not null && adoGitService is not null && config.Flow.OfferPrOnDone)
        {
            try
            {
                var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
                if (isInWorkTree)
                {
                    var currentBranch = await gitService.GetCurrentBranchAsync();
                    var isAhead = await gitService.IsAheadOfAsync(config.Git.DefaultTarget);

                    if (isAhead)
                    {
                        Console.Write($"Branch '{currentBranch}' is ahead of '{config.Git.DefaultTarget}'. Create PR? [y/N] ");
                        var response = consoleInput.ReadLine()?.Trim();
                        if (string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
                        {
                            var request = new PullRequestCreate(
                                SourceBranch: $"refs/heads/{currentBranch}",
                                TargetBranch: $"refs/heads/{config.Git.DefaultTarget}",
                                Title: $"#{item.Id} — {item.Title}",
                                Description: $"Resolves AB#{item.Id}.",
                                WorkItemId: item.Id);
                            createdPr = await adoGitService.CreatePullRequestAsync(request);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Git/PR operations are best-effort
            }
        }

        // 5. Print summary
        var actionStrings = new List<string>();
        if (workTreeSaved) actionStrings.Add("Work tree saved");
        if (newState is not null) actionStrings.Add($"State → {newState}");
        if (createdPr is not null) actionStrings.Add($"PR #{createdPr.PullRequestId} created");

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary(item.Id, item.Title, item.Type.Value, originalState, newState, workTreeSaved, createdPr));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            // Minimal: PR URL or empty (for script capture)
            Console.WriteLine(createdPr?.Url ?? "");
        }
        else
        {
            Console.WriteLine(fmt.FormatSuccess($"Flow done for #{item.Id} — {item.Title}"));
            foreach (var action in actionStrings)
                Console.WriteLine(fmt.FormatInfo($"  {action}"));
        }

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    private static string FormatJsonSummary(int id, string title, string type, string originalState, string? newState, bool saved, PullRequestInfo? pr)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "flow done");
        writer.WriteNumber("itemId", id);
        writer.WriteString("title", title);
        writer.WriteString("type", type);

        // Structured actions object
        writer.WriteStartObject("actions");
        writer.WriteBoolean("saved", saved);

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

        if (pr is not null)
        {
            writer.WriteStartObject("prCreated");
            writer.WriteNumber("id", pr.PullRequestId);
            writer.WriteString("url", pr.Url);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("prCreated");
        }

        writer.WriteEndObject(); // actions

        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
