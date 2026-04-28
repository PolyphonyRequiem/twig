using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig save</c>: pushes pending field changes and notes to ADO,
/// clears pending changes, and marks the item as clean.
/// Supports scoped save: active work tree (default), single item, or all dirty items.
/// Delegates all flush logic to <see cref="IPendingChangeFlusher"/>.
/// </summary>
public sealed class SaveCommand(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IPendingChangeFlusher pendingChangeFlusher,
    ActiveItemResolver activeItemResolver,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Push pending changes to Azure DevOps.</summary>
    /// <param name="targetId">When set, save only this single item.</param>
    /// <param name="all">When true, save all dirty items (legacy behavior).</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="skipPromptWrite">When true, suppresses the prompt state write. Useful for callers
    /// that perform their own prompt state write after additional operations.</param>
    public async Task<int> ExecuteAsync(
        int? targetId = null,
        bool all = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool skipPromptWrite = false,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        FlushResult result;

        if (all)
        {
            result = await pendingChangeFlusher.FlushAllAsync(outputFormat, ct);
        }
        else
        {
            IReadOnlyList<int> itemsToSave;

            if (targetId.HasValue)
            {
                itemsToSave = [targetId.Value];
            }
            else
            {
                // Active work tree mode: active item + dirty children
                var activeResult = await activeItemResolver.GetActiveItemAsync();
                if (!activeResult.TryGetWorkItem(out var activeItem, out var errorId, out var errorReason))
                {
                    _stderr.WriteLine(fmt.FormatError(errorId is not null
                        ? $"Work item #{errorId} not found in cache."
                        : "No active work item. Use 'twig save --all' or 'twig save <id>'."));
                    return 1;
                }

                var activeId = activeItem.Id;

                var dirtyIds = await pendingChangeStore.GetDirtyItemIdsAsync();
                if (dirtyIds.Count == 0)
                {
                    Console.WriteLine(fmt.FormatInfo("Nothing to save."));
                    return 0;
                }

                var children = await workItemRepo.GetChildrenAsync(activeId);
                var childIds = new HashSet<int>(children.Select(c => c.Id));
                itemsToSave = dirtyIds.Where(id => id == activeId || childIds.Contains(id)).ToList();
            }

            if (itemsToSave.Count == 0)
            {
                Console.WriteLine(fmt.FormatInfo("Nothing to save."));
                return 0;
            }

            result = await pendingChangeFlusher.FlushAsync(itemsToSave, outputFormat, ct);
        }

        // Handle cases with no dirty items (e.g., --all with nothing pending)
        if (result.ItemsFlushed == 0 && result.Failures.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("Nothing to save."));
            return 0;
        }

        if (result.ItemsFlushed > 0 && !skipPromptWrite && promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return result.Failures.Count > 0 ? 1 : 0;
    }
}