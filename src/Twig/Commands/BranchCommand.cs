using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;


namespace Twig.Commands;

/// <summary>
/// Implements <c>twig branch</c>: generates a branch name from the active work item,
/// creates/checks out the branch via <see cref="IGitService"/>, optionally adds an
/// ArtifactLink (Branch type) to the ADO work item, and auto-transitions state.
/// </summary>
public sealed class BranchCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IProcessConfigurationProvider processConfigProvider,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Create a branch from the active work item context.</summary>
    public async Task<int> ExecuteAsync(
        bool noLink = false,
        bool noTransition = false,
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

        // 3. Generate branch name and create/checkout
        var branchName = BranchNamingService.Generate(item, config.Git.BranchTemplate, config.Git.TypeMap);

        bool branchCreated;
        try
        {
            var branchExists = await gitService!.BranchExistsAsync(branchName);
            if (branchExists)
            {
                await gitService.CheckoutAsync(branchName);
                branchCreated = false;
            }
            else
            {
                await gitService.CreateBranchAsync(branchName);
                await gitService.CheckoutAsync(branchName);
                branchCreated = true;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Git branch operation failed: {ex.Message}"));
            return 1;
        }

        // 4. Artifact link (unless --no-link or autoLink disabled)
        bool linked = false;
        if (!noLink && config.Git.AutoLink && adoGitService is not null && branchCreated)
        {
            try
            {
                var projectId = await adoGitService.GetProjectIdAsync();
                var repoId = await adoGitService.GetRepositoryIdAsync();

                if (projectId is not null && repoId is not null)
                {
                    var encodedBranch = Uri.EscapeDataString(branchName);
                    var artifactUri = $"vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}";

                    // Fetch latest revision for optimistic concurrency
                    var remote = await adoService.FetchAsync(item.Id);
                    await adoGitService.AddArtifactLinkAsync(
                        item.Id, artifactUri, "ArtifactLink", remote.Revision, "Branch");
                    linked = true;
                }
            }
            catch (Exception)
            {
                // Artifact linking is best-effort — branch was already created
            }
        }

        // 5. Auto-transition state (unless --no-transition or autoTransition disabled)
        string? newState = null;
        string originalState = item.State;
        if (!noTransition && config.Git.AutoTransition)
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
                        try
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
                        catch (Exception)
                        {
                            newState = null; // State transition is best-effort
                        }

                        // Write prompt state outside try/catch — transition already committed if newState != null
                        if (newState is not null)
                            if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();
                    }
                }
            }
        }

        // 6. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonSummary(
                item.Id, item.Title, item.Type.Value, branchName, branchCreated, linked,
                originalState, newState));
        }
        else if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(branchName);
        }
        else
        {
            var verb = branchCreated ? "Created" : "Switched to";
            Console.WriteLine(fmt.FormatSuccess($"{verb} branch '{branchName}' for #{item.Id}"));

            if (linked)
                Console.WriteLine(fmt.FormatInfo("  Branch linked to work item"));
            if (newState is not null)
                Console.WriteLine(fmt.FormatInfo($"  State → {newState}"));

            var hints = hintEngine.GetHints("branch", item: item, outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    private static string FormatJsonSummary(
        int id, string title, string type, string branchName, bool branchCreated,
        bool linked, string originalState, string? newState)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "branch");
        writer.WriteNumber("itemId", id);
        writer.WriteString("title", title);
        writer.WriteString("type", type);

        writer.WriteStartObject("actions");

        writer.WriteStartObject("branch");
        writer.WriteString("name", branchName);
        writer.WriteBoolean("created", branchCreated);
        writer.WriteEndObject();

        writer.WriteBoolean("linked", linked);

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

        writer.WriteEndObject(); // actions

        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
