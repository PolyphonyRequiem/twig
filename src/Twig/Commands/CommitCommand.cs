using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;


namespace Twig.Commands;

/// <summary>
/// Implements <c>twig commit</c>: formats a commit message with work item context,
/// executes <c>git commit</c>, and optionally links the commit to the ADO work item.
/// Supports <c>--no-link</c> to skip artifact linking and pass-through of git flags.
/// </summary>
public sealed class CommitCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null)
{
    /// <summary>Commit with a work-item-enriched message.</summary>
    public async Task<int> ExecuteAsync(
        string? message = null,
        bool noLink = false,
        string[]? passthrough = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
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

        // 3. Format commit message
        var userMessage = message ?? string.Empty;
        var formattedMessage = CommitMessageService.Format(
            item, userMessage, config.Git.CommitTemplate, config.Git.TypeMap);

        // 4. Execute git commit with formatted message and pass-through args
        string commitHash;
        try
        {
            commitHash = await ExecuteGitCommitAsync(formattedMessage, passthrough);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Git commit failed: {ex.Message}"));
            return 1;
        }

        // 5. Artifact link (unless --no-link or autoLink disabled)
        bool linked = false;
        if (!noLink && config.Git.AutoLink && adoGitService is not null)
        {
            try
            {
                var projectId = await adoGitService.GetProjectIdAsync();
                var repoId = await adoGitService.GetRepositoryIdAsync();

                if (projectId is not null && repoId is not null)
                {
                    var artifactUri = $"vstfs:///Git/Commit/{projectId}/{repoId}/{commitHash}";
                    var remote = await adoService.FetchAsync(item.Id);
                    await adoGitService.AddArtifactLinkAsync(
                        item.Id, artifactUri, "ArtifactLink", remote.Revision, "Fixed in Commit");
                    linked = true;
                }
            }
            catch (Exception)
            {
                // Artifact linking is best-effort
            }
        }

        // 6. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary(item.Id, formattedMessage, commitHash, linked));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(commitHash);
        }
        else
        {
            Console.WriteLine(fmt.FormatSuccess($"Committed: {formattedMessage}"));
            Console.WriteLine(fmt.FormatInfo($"  {commitHash[..Math.Min(7, commitHash.Length)]}"));

            if (linked)
                Console.WriteLine(fmt.FormatInfo("  Commit linked to work item"));

            var hints = hintEngine.GetHints("commit", item: item, outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    private async Task<string> ExecuteGitCommitAsync(string formattedMessage, string[]? passthrough)
    {
        var extraArgs = new List<string>();

        if (passthrough is not null)
        {
            foreach (var arg in passthrough)
                extraArgs.Add(arg);
        }

        if (extraArgs.Count == 0)
            return await gitService!.CommitAsync(formattedMessage);

        // Forward all passthrough args (--amend, --, pathspecs, etc.) to git.
        return await gitService!.CommitWithArgsAsync(formattedMessage, extraArgs);
    }

    private static string FormatJsonSummary(int itemId, string message, string commitHash, bool linked)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "commit");
        writer.WriteNumber("itemId", itemId);
        writer.WriteString("message", message);
        writer.WriteString("commitHash", commitHash);
        writer.WriteBoolean("linked", linked);
        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
