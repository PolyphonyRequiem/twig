using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig context</c>: displays current branch, active work item,
/// detected work item from branch name, and linked PRs.
/// </summary>
public sealed class GitContextCommand(
    ActiveItemResolver activeItemResolver,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null)
{
    /// <summary>Show git context: branch, work item, and PR linkage.</summary>
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Active work item context
        var resolved = await activeItemResolver.GetActiveItemAsync();
        int? activeId = null;
        string? activeTitle = null;
        string? activeType = null;
        if (resolved.TryGetWorkItem(out var contextItem, out var unreachableId, out var unreachableReason))
        {
            activeId = contextItem!.Id;
            activeTitle = contextItem.Title;
            activeType = contextItem.Type.Value;
        }
        else if (unreachableId is not null)
        {
            activeId = unreachableId;
            Console.Error.WriteLine(fmt.FormatError($"Work item #{unreachableId} is unreachable: {unreachableReason}"));
        }

        // 2. Current branch (if git available)
        string? branchName = null;
        int? detectedId = null;
        bool isInGitRepo = false;

        if (gitService is not null)
        {
            try
            {
                isInGitRepo = await gitService.IsInsideWorkTreeAsync();
                if (isInGitRepo)
                {
                    branchName = await gitService.GetCurrentBranchAsync();
                    detectedId = WorkItemIdExtractor.Extract(branchName, config.Git.BranchPattern);
                }
            }
            catch
            {
                // Git operations are best-effort
            }
        }

        // 3. Linked PRs (if git service + branch available)
        var prs = new List<(int Id, string Title, string Status)>();
        if (adoGitService is not null && branchName is not null)
        {
            try
            {
                var prList = await adoGitService.GetPullRequestsForBranchAsync(branchName);
                foreach (var pr in prList)
                    prs.Add((pr.PullRequestId, pr.Title, pr.Status));
            }
            catch
            {
                // PR lookup is best-effort
            }
        }

        // 4. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJson(activeId, activeTitle, activeType, branchName, detectedId, prs));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            if (branchName is not null)
                Console.WriteLine(branchName);
            if (activeId.HasValue)
                Console.WriteLine(activeId.Value);
        }
        else
        {
            // Human format
            if (branchName is not null)
                Console.WriteLine(fmt.FormatInfo($"Branch: {branchName}"));
            else
                Console.WriteLine(fmt.FormatInfo("Branch: (not in a git repository)"));

            if (activeId.HasValue)
            {
                var desc = activeTitle is not null ? $"#{activeId.Value} ({activeType}: {activeTitle})" : $"#{activeId.Value}";
                Console.WriteLine(fmt.FormatInfo($"Context: {desc}"));
            }
            else
            {
                Console.WriteLine(fmt.FormatInfo("Context: (none)"));
            }

            if (detectedId.HasValue && detectedId != activeId)
            {
                Console.WriteLine(fmt.FormatInfo($"Detected from branch: #{detectedId.Value}"));
            }

            if (prs.Count > 0)
            {
                Console.WriteLine(fmt.FormatInfo("Linked PRs:"));
                foreach (var (id, title, status) in prs)
                    Console.WriteLine(fmt.FormatInfo($"  PR !{id}: {title} [{status}]"));
            }

            var hints = hintEngine.GetHints("context", outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    private static string FormatJson(
        int? activeId, string? activeTitle, string? activeType,
        string? branchName, int? detectedId,
        List<(int Id, string Title, string Status)> prs)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "context");

        if (branchName is not null)
            writer.WriteString("branch", branchName);
        else
            writer.WriteNull("branch");

        if (activeId.HasValue)
        {
            writer.WriteStartObject("activeWorkItem");
            writer.WriteNumber("id", activeId.Value);
            if (activeTitle is not null) writer.WriteString("title", activeTitle);
            if (activeType is not null) writer.WriteString("type", activeType);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("activeWorkItem");
        }

        if (detectedId.HasValue)
            writer.WriteNumber("detectedWorkItemId", detectedId.Value);
        else
            writer.WriteNull("detectedWorkItemId");

        writer.WriteStartArray("pullRequests");
        foreach (var (id, title, status) in prs)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("title", title);
            writer.WriteString("status", status);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
