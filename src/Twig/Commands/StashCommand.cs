using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;


namespace Twig.Commands;

/// <summary>
/// Implements <c>twig stash</c> and <c>twig stash pop</c>: wraps git stash with
/// work item context in the stash message, and restores Twig context on pop.
/// </summary>
public sealed class StashCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    ActiveItemResolver activeItemResolver,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Stash changes with work item context in the message.</summary>
    public async Task<int> ExecuteAsync(string? message = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Check git availability
        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, fmt);
        if (!isValid) return exitCode;

        // 2. Build stash message with work item context
        var resolved = await activeItemResolver.GetActiveItemAsync();
        int? activeId = null;
        string stashMessage;
        if (resolved.TryGetWorkItem(out var workItem, out var errorId, out var errorReason))
        {
            activeId = workItem!.Id;
            var itemContext = $"[#{workItem.Id} {workItem.Title}]";
            stashMessage = message is not null
                ? $"{itemContext} {message}"
                : itemContext;
        }
        else if (errorId is not null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{errorId} is unreachable: {errorReason}"));
            return 1;
        }
        else
        {
            stashMessage = message ?? "twig stash";
        }

        // 3. Execute git stash
        try
        {
            await gitService!.StashAsync(stashMessage);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Git stash failed: {ex.Message}"));
            return 1;
        }

        // 4. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary("stash", stashMessage, activeId));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(stashMessage);
        }
        else
        {
            Console.WriteLine(fmt.FormatSuccess($"Stashed: {stashMessage}"));

            var hints = hintEngine.GetHints("stash", outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    /// <summary>Pop the most recent stash and restore Twig context.</summary>
    public async Task<int> PopAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Check git availability
        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, fmt);
        if (!isValid) return exitCode;

        // 2. Execute git stash pop
        try
        {
            await gitService!.StashPopAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Git stash pop failed: {ex.Message}"));
            return 1;
        }

        // 3. Attempt to restore Twig context from branch name
        int? detectedId = null;
        try
        {
            var branchName = await gitService.GetCurrentBranchAsync();
            detectedId = WorkItemIdExtractor.Extract(branchName, config.Git.BranchPattern);
            if (detectedId.HasValue)
            {
                var exists = await workItemRepo.ExistsByIdAsync(detectedId.Value);
                if (exists)
                {
                    await contextStore.SetActiveWorkItemIdAsync(detectedId.Value);
                    if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();
                }
                else
                {
                    detectedId = null;
                }
            }
        }
        catch
        {
            // Context restoration is best-effort
        }

        // 4. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary("stash-pop", null, detectedId));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("popped");
        }
        else
        {
            Console.WriteLine(fmt.FormatSuccess("Stash popped."));
            if (detectedId.HasValue)
            {
                var restoredItem = await workItemRepo.GetByIdAsync(detectedId.Value, ct);
                var restoredLabel = restoredItem is not null ? $"#{detectedId.Value} {restoredItem.Title}" : $"#{detectedId.Value}";
                Console.WriteLine(fmt.FormatInfo($"  Context restored to {restoredLabel}"));
            }

            var hints = hintEngine.GetHints("stash", outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    private static string FormatJsonSummary(string action, string? message, int? workItemId)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", action);
        if (message is not null)
            writer.WriteString("message", message);
        else
            writer.WriteNull("message");
        if (workItemId.HasValue)
            writer.WriteNumber("workItemId", workItemId.Value);
        else
            writer.WriteNull("workItemId");
        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
