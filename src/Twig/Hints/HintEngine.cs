using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Hints;

/// <summary>
/// Provides contextual hint strings after command execution.
/// Hints are suppressed when <c>config.display.hints</c> is false
/// or when using JSON/minimal output formats.
/// </summary>
public sealed class HintEngine
{
    private readonly bool _hintsEnabled;

    public HintEngine(DisplayConfig displayConfig)
    {
        _hintsEnabled = displayConfig.Hints;
    }

    /// <summary>
    /// Returns contextual hints for the given command. Returns empty if hints are disabled.
    /// </summary>
    /// <param name="commandName">The CLI command that just executed (e.g., "set", "state", "seed").</param>
    /// <param name="item">The active work item, if available.</param>
    /// <param name="workspace">The workspace model, if available.</param>
    /// <param name="outputFormat">The output format being used ("human", "json", "minimal").</param>
    /// <param name="stateShorthand">The state shorthand code used (for "state" command).</param>
    /// <param name="createdId">The ID of a newly created item (for "seed" command).</param>
    /// <param name="siblings">Sibling work items (for "state d" hint about all siblings done).</param>
    /// <param name="staleSeedCount">Number of stale seeds (for "status" command).</param>
    public IReadOnlyList<string> GetHints(
        string commandName,
        WorkItem? item = null,
        Workspace? workspace = null,
        string outputFormat = "human",
        string? stateShorthand = null,
        int? createdId = null,
        IReadOnlyList<WorkItem>? siblings = null,
        int staleSeedCount = 0)
    {
        // Suppress hints for non-human formats or when disabled
        if (!_hintsEnabled)
            return Array.Empty<string>();

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        var hints = new List<string>();

        switch (commandName.ToLowerInvariant())
        {
            case "set":
                hints.Add("Try: twig status, twig tree, twig state <shorthand>");
                break;

            case "state":
                if (string.Equals(stateShorthand, "d", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if all siblings are done
                    if (siblings is not null && siblings.Count > 0)
                    {
                        var allSiblingsDone = true;
                        foreach (var sibling in siblings)
                        {
                            var state = sibling.State.ToLowerInvariant();
                            if (state is not ("closed" or "done" or "resolved" or "removed"))
                            {
                                allSiblingsDone = false;
                                break;
                            }
                        }

                        if (allSiblingsDone)
                        {
                            hints.Add("All sibling tasks complete. Consider: twig up then twig state d");
                        }
                    }

                    // Check for pending notes
                    if (item is not null && item.PendingNotes.Count > 0)
                    {
                        hints.Add($"You have {item.PendingNotes.Count} pending notes. Run twig save to push them.");
                    }
                }
                else if (string.Equals(stateShorthand, "x", StringComparison.OrdinalIgnoreCase))
                {
                    hints.Add("Item cut. Consider: twig up to return to parent");
                }
                break;

            case "seed":
                if (createdId.HasValue)
                {
                    hints.Add($"Created #{createdId.Value}. Try: twig set {createdId.Value} to switch context");
                }
                break;

            case "note":
                hints.Add("Note staged. Will push on next twig update or twig save");
                break;

            case "edit":
                hints.Add("Changes staged locally. Run twig save to persist to ADO");
                break;

            case "status":
                if (staleSeedCount > 0)
                {
                    var noun = staleSeedCount == 1 ? "seed" : "seeds";
                    hints.Add($"⚠ {staleSeedCount} stale {noun}. Consider completing or cutting them.");
                }
                break;

            case "init":
                hints.Add("Run 'twig set <id>' to set your active work item.");
                break;

            case "workspace":
                if (workspace is not null)
                {
                    var dirtyItems = workspace.GetDirtyItems();
                    if (dirtyItems.Count > 0)
                    {
                        var noun = dirtyItems.Count == 1 ? "item" : "items";
                        hints.Add($"{dirtyItems.Count} dirty {noun}. Run twig save to push changes.");
                    }
                }
                break;
        }

        return hints;
    }

    /// <summary>
    /// Checks the current git branch name for a matching work item ID in the local cache.
    /// Returns a hint string if the branch matches a known work item and no active context is set.
    /// Returns null if no hint should be shown.
    /// </summary>
    /// <param name="activeContextId">The current active context work item ID, or null if none.</param>
    /// <param name="gitService">Git service for branch detection. If null, returns null.</param>
    /// <param name="workItemRepo">Repository for checking if the ID exists in cache.</param>
    /// <param name="branchPattern">Regex pattern for extracting work item IDs from branch names.</param>
    /// <param name="outputFormat">The output format being used.</param>
    public async Task<string?> GetBranchDetectionHintAsync(
        int? activeContextId,
        IGitService? gitService,
        IWorkItemRepository workItemRepo,
        string branchPattern,
        string outputFormat = "human")
    {
        if (!_hintsEnabled)
            return null;

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
            return null;

        // Only emit when no active context is set
        if (activeContextId.HasValue)
            return null;

        if (gitService is null)
            return null;

        try
        {
            var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
            if (!isInWorkTree)
                return null;

            var branchName = await gitService.GetCurrentBranchAsync();
            var extractedId = BranchNameTemplate.ExtractWorkItemId(branchName, branchPattern);
            if (extractedId is null)
                return null;

            var exists = await workItemRepo.ExistsByIdAsync(extractedId.Value);
            if (!exists)
                return null;

            return $"Tip: branch matches #{extractedId.Value}. Run 'twig set {extractedId.Value}' to set context.";
        }
        catch (Exception)
        {
            // Git operations are best-effort — never fail on hint detection
            return null;
        }
    }
}
