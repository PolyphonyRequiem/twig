using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig flow-close</c>: guards unsaved changes and open PRs, transitions to Completed,
/// deletes the local branch, and clears the active context.
/// </summary>
public sealed class FlowCloseCommand(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IContextStore contextStore,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null)
{
    /// <summary>Close a work item: guard, transition to Completed, delete branch, clear context.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool force = false,
        bool noBranchCleanup = false,
        string outputFormat = "human")
    {
        _ = hintEngine; // No registered hints for flow-close yet

        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Resolve target
        int targetId;

        if (id.HasValue)
        {
            targetId = id.Value;
        }
        else
        {
            var activeId = await contextStore.GetActiveWorkItemIdAsync();
            if (!activeId.HasValue)
            {
                Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig flow-start <id>' first."));
                return 1;
            }
            targetId = activeId.Value;
        }

        var item = await workItemRepo.GetByIdAsync(targetId);
        if (item is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{targetId} not found in cache."));
            return 1;
        }

        // 2. Guard: unsaved changes
        if (!force)
        {
            var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync();
            var dirtySet = new HashSet<int>(dirtyIds);
            if (dirtySet.Contains(targetId))
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Work item #{targetId} has unsaved changes. Run 'twig save' first or use --force."));
                return 1;
            }
        }

        // 3. Guard: open PRs
        string? currentBranch = null;
        if (!force && gitService is not null && adoGitService is not null)
        {
            try
            {
                var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
                if (isInWorkTree)
                {
                    currentBranch = await gitService.GetCurrentBranchAsync();
                    var prs = await adoGitService.GetPullRequestsForBranchAsync(currentBranch);
                    var activePrs = prs.Where(p =>
                        string.Equals(p.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (activePrs.Count > 0)
                    {
                        var prList = string.Join(", ", activePrs.Select(p => $"PR #{p.PullRequestId}"));

                        if (consoleInput.IsOutputRedirected)
                        {
                            // Non-TTY: exit 2
                            Console.Error.WriteLine(fmt.FormatError(
                                $"Open pull request(s) detected: {prList}. Complete or abandon before closing."));
                            return 2;
                        }

                        Console.Write($"Open PR(s) detected: {prList}. Continue anyway? [y/N] ");
                        var response = consoleInput.ReadLine()?.Trim();
                        if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine(fmt.FormatInfo("Cancelled."));
                            return 0;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Git/PR operations are best-effort
            }
        }

        // 4. Transition to Completed
        string? newState = null;
        string originalState = item.State;
        var processConfig = processConfigProvider.GetConfiguration();
        if (processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
        {
            var category = StateCategoryResolver.Resolve(item.State, typeConfig.StateEntries);

            // Skip if already Completed
            if (category != StateCategory.Completed)
            {
                var resolveResult = StateShorthand.Resolve('d', typeConfig.StateEntries);
                if (resolveResult.IsSuccess)
                {
                    newState = resolveResult.Value;
                    var remote = await adoService.FetchAsync(item.Id);
                    var changes = new[] { new FieldChange("System.State", item.State, newState) };
                    var newRevision = await adoService.PatchAsync(item.Id, changes, remote.Revision);
                    item.ChangeState(newState);
                    item.ApplyCommands();
                    item.MarkSynced(newRevision);
                    await workItemRepo.SaveAsync(item);
                }
            }
        }

        // 5. Branch cleanup (prompt then delete)
        bool branchDeleted = false;
        if (!noBranchCleanup && gitService is not null)
        {
            try
            {
                var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
                if (isInWorkTree)
                {
                    currentBranch ??= await gitService.GetCurrentBranchAsync();
                    var defaultTarget = config.Git.DefaultTarget;

                    // Only delete if not already on the default branch
                    if (!string.Equals(currentBranch, defaultTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Write($"Delete branch '{currentBranch}'? [y/N] ");
                        var response = consoleInput.ReadLine()?.Trim();
                        if (string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
                        {
                            await gitService.CheckoutAsync(defaultTarget);
                            await gitService.DeleteBranchAsync(currentBranch);
                            branchDeleted = true;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // DD-007: Git operations are skipped, not errored
            }
        }

        // 6. Clear context
        await contextStore.ClearActiveWorkItemIdAsync();

        // 7. Print summary
        var actionStrings = new List<string>();
        if (newState is not null) actionStrings.Add($"State → {newState}");
        if (branchDeleted) actionStrings.Add($"Branch '{currentBranch}' deleted");
        actionStrings.Add("Context cleared");

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary(item.Id, item.Title, item.Type.Value, originalState, newState, branchDeleted, currentBranch));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            // Minimal: empty (for script capture)
            Console.WriteLine("");
        }
        else
        {
            Console.WriteLine(fmt.FormatSuccess($"Flow closed for #{item.Id} — {item.Title}"));
            foreach (var action in actionStrings)
                Console.WriteLine(fmt.FormatInfo($"  {action}"));
        }

        return 0;
    }

    private static string FormatJsonSummary(int id, string title, string type, string originalState, string? newState, bool branchDeleted, string? branch)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("command", "flow close");
        writer.WriteNumber("itemId", id);
        writer.WriteString("title", title);
        writer.WriteString("type", type);

        // Structured actions object
        writer.WriteStartObject("actions");

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

        if (branch is not null)
        {
            writer.WriteStartObject("branch");
            writer.WriteString("name", branch);
            writer.WriteBoolean("deleted", branchDeleted);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("branch");
        }

        writer.WriteBoolean("contextCleared", true);

        writer.WriteEndObject(); // actions

        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
