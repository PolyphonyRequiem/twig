using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig save</c>: pushes pending field changes and notes to ADO,
/// clears pending changes, and marks the item as clean.
/// Supports scoped save: active work tree (default), single item, or all dirty items.
/// Delegates all flush logic to <see cref="IPendingChangeFlusher"/>.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// "Nothing to save" emits a "saveNothingPending" record. Success/failure remains
/// signaled via exit code (flush layer owns its own output).
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class SaveCommand(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IPendingChangeFlusher pendingChangeFlusher,
    ActiveItemResolver activeItemResolver,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    TextWriter? stderr = null,
    RendererFactory? rendererFactory = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Push pending changes to Azure DevOps.</summary>
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
                    RenderNothingToSave(outputFormat);
                    return 0;
                }

                var children = await workItemRepo.GetChildrenAsync(activeId);
                var childIds = new HashSet<int>(children.Select(c => c.Id));
                itemsToSave = dirtyIds.Where(id => id == activeId || childIds.Contains(id)).ToList();
            }

            if (itemsToSave.Count == 0)
            {
                RenderNothingToSave(outputFormat);
                return 0;
            }

            result = await pendingChangeFlusher.FlushAsync(itemsToSave, outputFormat, ct);
        }

        if (result.ItemsFlushed == 0 && result.Failures.Count == 0)
        {
            RenderNothingToSave(outputFormat);
            return 0;
        }

        if (result.ItemsFlushed > 0 && !skipPromptWrite && promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return result.Failures.Count > 0 ? 1 : 0;
    }

    private void RenderNothingToSave(string outputFormat)
    {
        const string message = "Nothing to save.";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("saveNothingPending", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}