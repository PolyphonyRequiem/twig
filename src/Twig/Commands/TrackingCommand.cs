using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements the five tracking sub-commands under <c>twig workspace</c>:
/// <c>track &lt;id&gt;</c>, <c>track-tree &lt;id&gt;</c>, <c>untrack &lt;id&gt;</c>,
/// <c>exclude &lt;id&gt;</c>, and <c>exclusions</c>.
/// Delegates to <see cref="ITrackingService"/> for persistence.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// each outcome emits a per-format record (tracked, untracked, excluded, etc.). The
/// exclusions list emits a Document with an entries Table on machine formats and
/// streamed lines on human format (mirrors the SprintCommand list pattern).
/// <see cref="OutputFormatterFactory"/> is retained only for stderr errors.
/// </remarks>
public sealed class TrackingCommand(
    ITrackingService trackingService,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Track a single work item by ID.</summary>
    public async Task<int> TrackAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await TrackCoreAsync(id, TrackingMode.Single, outputFormat, ct);

    /// <summary>Track a work item and its subtree by ID.</summary>
    public async Task<int> TrackTreeAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await TrackCoreAsync(id, TrackingMode.Tree, outputFormat, ct);

    /// <summary>Remove a work item from tracking.</summary>
    public async Task<int> UntrackAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (id <= 0)
        {
            Console.Error.WriteLine(fmt.FormatError("Cannot untrack seeds or invalid IDs. Provide a positive work item ID."));
            return 2;
        }

        var wasTracked = await trackingService.UntrackAsync(id, ct);
        if (wasTracked)
            RenderOutcome("untracked", $"Untracked #{id}.", id, outputFormat, Severity.Success);
        else
            RenderOutcome("untrackNotTracked", $"#{id} was not tracked.", id, outputFormat, Severity.Info);

        return 0;
    }

    /// <summary>Exclude a work item from workspace view.</summary>
    public async Task<int> ExcludeAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (id <= 0)
        {
            Console.Error.WriteLine(fmt.FormatError("Cannot exclude seeds or invalid IDs. Provide a positive work item ID."));
            return 2;
        }

        await trackingService.ExcludeAsync(id, ct);
        RenderOutcome("excluded", $"Excluded #{id} from workspace view.", id, outputFormat, Severity.Success);
        return 0;
    }

    /// <summary>List all exclusions, or clear/remove exclusions.</summary>
    public async Task<int> ExclusionsAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool clear = false, int? remove = null, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (clear)
        {
            var count = await trackingService.ClearExclusionsAsync(ct);
            if (count > 0)
                RenderOutcome("exclusionsCleared", $"Cleared {count} exclusion(s).", null, outputFormat, Severity.Success, extra: ("count", RenderCell.Integer(count)));
            else
                RenderOutcome("exclusionsClearedEmpty", "No exclusions to clear.", null, outputFormat, Severity.Info);
            return 0;
        }

        if (remove is { } removeId)
        {
            if (removeId <= 0)
            {
                Console.Error.WriteLine(fmt.FormatError("Provide a positive work item ID to remove."));
                return 2;
            }

            var wasExcluded = await trackingService.RemoveExclusionAsync(removeId, ct);
            if (wasExcluded)
                RenderOutcome("exclusionRemoved", $"Removed exclusion for #{removeId}.", removeId, outputFormat, Severity.Success);
            else
                RenderOutcome("exclusionRemoveNotFound", $"#{removeId} was not excluded.", removeId, outputFormat, Severity.Info);
            return 0;
        }

        var exclusions = await trackingService.ListExclusionsAsync(ct);
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();

        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var columns = new List<RenderColumn>
            {
                new("id", "ID"),
                new("title", "Title"),
            };
            var rows = new List<RenderRow>(exclusions.Count);
            foreach (var item in exclusions)
            {
                var title = await GetTitleAsync(item.WorkItemId, ct);
                var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["id"] = RenderCell.Integer(item.WorkItemId),
                };
                if (title is not null)
                    cells["title"] = RenderCell.String(title);
                rows.Add(new RenderRow("exclusion", cells));
            }
            var fields = new List<DocumentField>(2)
            {
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(exclusions.Count))),
                new("entries", new RenderNode.Table(null, columns, rows)),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Document("exclusionsList", fields),
            }));
            return 0;
        }

        if (exclusions.Count == 0)
        {
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Text("No exclusions configured.", Severity.Info),
            }));
            return 0;
        }

        var humanNodes = new List<RenderNode>(exclusions.Count + 1);
        foreach (var item in exclusions)
        {
            var title = await GetTitleAsync(item.WorkItemId, ct);
            var display = title is not null
                ? $"#{item.WorkItemId}: {title}"
                : $"#{item.WorkItemId}";
            humanNodes.Add(new RenderNode.Text(display, Severity.Info));
        }
        humanNodes.Add(new RenderNode.Text($"{exclusions.Count} exclusion(s) total.", Severity.Info));
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(humanNodes));
        return 0;
    }

    private async Task<int> TrackCoreAsync(int id, TrackingMode mode, string outputFormat, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (id <= 0)
        {
            Console.Error.WriteLine(fmt.FormatError("Cannot track seeds or invalid IDs. Provide a positive work item ID."));
            return 2;
        }

        if (mode == TrackingMode.Tree)
            await trackingService.TrackTreeAsync(id, ct);
        else
            await trackingService.TrackAsync(id, mode, ct);

        var title = await GetTitleAsync(id, ct);
        var modeLabel = mode == TrackingMode.Tree ? " (tree)" : "";
        var display = title is not null
            ? $"Tracking #{id}: {title}{modeLabel}"
            : $"Tracking #{id}{modeLabel}";

        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(display),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildTrackRecord(id, title, mode, display),
            _ => new RenderNode.Text(display, Severity.Success),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
        return 0;
    }

    private static RenderNode BuildTrackRecord(int id, string? title, TrackingMode mode, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["itemId"] = RenderCell.Integer(id),
            ["mode"] = RenderCell.String(mode.ToString()),
            ["message"] = RenderCell.String(message),
        };
        if (title is not null)
            fields["title"] = RenderCell.String(title);
        return new RenderNode.Record("tracked", fields);
    }

    private void RenderOutcome(string kind, string message, int? itemId, string outputFormat, Severity severity, (string Key, RenderCell Value)? extra = null)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" => BuildOutcomeRecord(kind, message, itemId, extra),
            _ => new RenderNode.Text(message, severity),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static RenderNode BuildOutcomeRecord(string kind, string message, int? itemId, (string Key, RenderCell Value)? extra)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["message"] = RenderCell.String(message),
        };
        if (itemId.HasValue)
            fields["itemId"] = RenderCell.Integer(itemId.Value);
        if (extra is { } e)
            fields[e.Key] = e.Value;
        return new RenderNode.Record(kind, fields);
    }

    private async Task<string?> GetTitleAsync(int id, CancellationToken ct)
    {
        var item = await workItemRepo.GetByIdAsync(id, ct);
        return item?.Title;
    }
}
