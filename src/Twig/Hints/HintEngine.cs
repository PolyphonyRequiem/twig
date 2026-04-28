using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Process;
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
    private readonly IProcessConfigurationProvider? _processConfigProvider;

    public HintEngine(DisplayConfig displayConfig, IProcessConfigurationProvider? processConfigProvider = null)
    {
        _hintsEnabled = displayConfig.Hints;
        _processConfigProvider = processConfigProvider;
    }

    /// <summary>
    /// Returns contextual hints for the given command. Returns empty if hints are disabled.
    /// </summary>
    /// <param name="commandName">The CLI command that just executed (e.g., "set", "state", "seed").</param>
    /// <param name="item">The active work item, if available.</param>
    /// <param name="workspace">The workspace model, if available.</param>
    /// <param name="outputFormat">The output format being used ("human", "json", "minimal").</param>
    /// <param name="newStateName">The resolved state name (for "state" command).</param>
    /// <param name="createdId">The ID of a newly created item (for "seed" command).</param>
    /// <param name="siblings">Sibling work items (for "state d" hint about all siblings done).</param>
    /// <param name="staleSeedCount">Number of stale seeds (for "status" command).</param>
    public IReadOnlyList<string> GetHints(
        string commandName,
        WorkItem? item = null,
        Workspace? workspace = null,
        string outputFormat = "human",
        string? newStateName = null,
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
                hints.Add("Try: twig show, twig tree, twig state <name>");
                if (item?.ParentId.HasValue == true)
                    hints.Add("Siblings: twig next, twig prev");
                break;

            case "state":
            {
                var stateEntries = item is not null
                    ? _processConfigProvider.SafeGetConfiguration(item.Type.Value)?.StateEntries
                    : null;
                var category = StateCategoryResolver.Resolve(newStateName, stateEntries);
                if (category == StateCategory.Completed)
                {
                    // Check if all siblings are done
                    if (siblings is not null && siblings.Count > 0)
                    {
                        var allSiblingsDone = true;
                        foreach (var sibling in siblings)
                        {
                            var siblingEntries = _processConfigProvider.SafeGetConfiguration(sibling.Type.Value)?.StateEntries;
                            var siblingCategory = StateCategoryResolver.Resolve(sibling.State, siblingEntries);
                            if (siblingCategory is not (StateCategory.Completed or StateCategory.Resolved or StateCategory.Removed))
                            {
                                allSiblingsDone = false;
                                break;
                            }
                        }

                        if (allSiblingsDone)
                        {
                            var completedStateName = stateEntries?.FirstOrDefault(e => e.Category == StateCategory.Completed).Name ?? "Done";
                            hints.Add($"All sibling tasks complete. Consider: twig up then twig state {completedStateName}");
                        }
                    }

                    // Check for pending notes
                    if (item is not null && item.PendingNotes.Count > 0)
                    {
                        hints.Add($"You have {item.PendingNotes.Count} pending notes. Run twig save to push them.");
                    }
                }
                else if (category == StateCategory.Removed)
                {
                    hints.Add("Item cut. Consider: twig up to return to parent");
                }
                break;
            }

            case "seed":
                if (createdId.HasValue)
                {
                    hints.Add($"Created local seed #{createdId.Value}. Try: twig seed edit {createdId.Value}, twig seed view");
                }
                break;

            case "seed-chain":
                hints.Add("Try: twig seed view, twig seed links");
                break;

            case "note":
                hints.Add("Note staged. Will push on next twig update or twig save");
                break;

            case "edit":
                hints.Add("Changes staged locally. Run twig save to persist to ADO");
                break;

            case "init":
                hints.Add("Run 'twig workspace' to see your sprint, or 'twig set <id>' to focus on an item.");
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

            case "query":
                hints.Add("Use 'twig set <id>' to navigate to an item.");
                hints.Add("Use 'twig show <id>' to view item details.");
                hints.Add("Use '--output ids' to pipe IDs to other commands.");
                break;

            case "next":
            case "prev":
                hints.Add("Try: twig next, twig prev, twig up, twig show");
                break;
        }

        return hints;
    }

}