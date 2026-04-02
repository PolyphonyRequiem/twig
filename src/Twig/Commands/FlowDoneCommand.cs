using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig flow-done</c>: flushes active work tree, transitions to Resolved,
/// and offers PR creation if the branch is ahead of the default target.
/// </summary>
public sealed class FlowDoneCommand(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    PendingChangeFlusher pendingChangeFlusher,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    TwigConfiguration config,
    FlowTransitionService flowTransitionService,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Mark a work item as done: flush work tree, transition to Resolved, offer PR.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool noSave = false,
        bool noPr = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Resolve target via FlowTransitionService
        var resolveResult = await flowTransitionService.ResolveItemAsync(id, ct);
        if (!resolveResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(resolveResult.ErrorMessage!));
            return 1;
        }

        var item = resolveResult.Item!;
        int targetId = item.Id;
        bool isExplicitId = resolveResult.IsExplicitId;

        // 2. Flush work tree (if not --no-save)
        bool workTreeSaved = false;
        if (!noSave)
        {
            var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync(ct);
            IReadOnlyList<int> itemsToFlush;

            if (isExplicitId)
            {
                // Explicit ID: flush single item only if dirty
                itemsToFlush = dirtyIds.Contains(targetId) ? [targetId] : [];
            }
            else
            {
                // No explicit ID: flush active work tree — scope to active item + children
                var children = await workItemRepo.GetChildrenAsync(targetId, ct);
                var childIds = new HashSet<int>(children.Select(c => c.Id));
                itemsToFlush = dirtyIds.Where(d => d == targetId || childIds.Contains(d)).ToList();
            }

            if (itemsToFlush.Count > 0)
            {
                var flushResult = await pendingChangeFlusher.FlushAsync(itemsToFlush, outputFormat, ct);
                if (flushResult.Failures.Count > 0)
                    return 1;
                workTreeSaved = true;
            }
        }

        // 3. Transition state via FlowTransitionService: InProgress → Resolved (or Completed fallback)
        string? newState = null;
        string originalState = item.State;
        var transitionResult = await flowTransitionService.TransitionStateAsync(
            item, StateCategory.Resolved, StateCategory.Completed, ct);
        if (transitionResult.Transitioned)
        {
            newState = transitionResult.NewState;
            originalState = transitionResult.OriginalState;
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
