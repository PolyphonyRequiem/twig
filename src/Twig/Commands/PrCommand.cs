using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;


namespace Twig.Commands;

/// <summary>
/// Implements <c>twig pr</c>: creates an ADO pull request linked to the active work item.
/// Determines source/target branches, builds PR title/description from work item fields,
/// and supports <c>--target</c>, <c>--title</c>, and <c>--draft</c> flags.
/// </summary>
public sealed class PrCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null)
{
    /// <summary>Create a pull request from the active work item context.</summary>
    public async Task<int> ExecuteAsync(
        string? target = null,
        string? title = null,
        bool draft = false,
        string outputFormat = "human")
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Resolve active work item
        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            Console.Error.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} is unreachable: {errorReason}"
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // 2. Check git availability
        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, fmt);
        if (!isValid) return exitCode;

        // 3. Check ADO Git service availability
        if (adoGitService is null)
        {
            Console.Error.WriteLine(fmt.FormatError("ADO Git service is not configured. Set git.project and git.repository in config."));
            return 1;
        }

        // 4. Determine source and target branches
        string sourceBranch;
        try
        {
            sourceBranch = await gitService!.GetCurrentBranchAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Failed to get current branch: {ex.Message}"));
            return 1;
        }

        var targetBranch = target ?? config.Git.DefaultTarget;

        // 5. Build PR title and description
        var prTitle = title ?? $"#{item.Id} — {item.Title}";
        var prDescription = $"Resolves AB#{item.Id}.\n\n**Type:** {item.Type}\n**State:** {item.State}";

        // 6. Create pull request
        PullRequestInfo createdPr;
        try
        {
            var request = new PullRequestCreate(
                SourceBranch: $"refs/heads/{sourceBranch}",
                TargetBranch: $"refs/heads/{targetBranch}",
                Title: prTitle,
                Description: prDescription,
                WorkItemId: item.Id,
                IsDraft: draft);

            createdPr = await adoGitService.CreatePullRequestAsync(request);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Failed to create pull request: {ex.Message}"));
            return 1;
        }

        // 7. Add PR artifact link to work item (best-effort)
        bool linked = false;
        if (config.Git.AutoLink)
        {
            try
            {
                var projectId = await adoGitService.GetProjectIdAsync();
                var repoId = await adoGitService.GetRepositoryIdAsync();

                if (projectId is not null && repoId is not null)
                {
                    var artifactUri = $"vstfs:///Git/PullRequestId/{projectId}/{repoId}/{createdPr.PullRequestId}";
                    var remote = await adoService.FetchAsync(item.Id);
                    await adoGitService.AddArtifactLinkAsync(
                        item.Id, artifactUri, "ArtifactLink", remote.Revision, "Pull Request");
                    linked = true;
                }
            }
            catch (Exception)
            {
                // Artifact linking is best-effort
            }
        }

        // 8. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary(item.Id, createdPr, linked, draft));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(createdPr.Url);
        }
        else
        {
            var draftLabel = draft ? " (draft)" : "";
            Console.WriteLine(fmt.FormatSuccess($"PR #{createdPr.PullRequestId} created{draftLabel} for #{item.Id}"));
            Console.WriteLine(fmt.FormatInfo($"  {prTitle}"));
            Console.WriteLine(fmt.FormatInfo($"  {sourceBranch} → {targetBranch}"));

            if (linked)
                Console.WriteLine(fmt.FormatInfo("  PR linked to work item"));

            if (!string.IsNullOrEmpty(createdPr.Url))
                Console.WriteLine(fmt.FormatInfo($"  {createdPr.Url}"));

            var hints = hintEngine.GetHints("pr", item: item, outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    private static string FormatJsonSummary(int itemId, PullRequestInfo pr, bool linked, bool draft)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "pr");
        writer.WriteNumber("itemId", itemId);

        writer.WriteStartObject("pullRequest");
        writer.WriteNumber("id", pr.PullRequestId);
        writer.WriteString("title", pr.Title);
        writer.WriteString("url", pr.Url);
        writer.WriteString("sourceBranch", pr.SourceBranch);
        writer.WriteString("targetBranch", pr.TargetBranch);
        writer.WriteBoolean("isDraft", draft);
        writer.WriteEndObject();

        writer.WriteBoolean("linked", linked);
        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
