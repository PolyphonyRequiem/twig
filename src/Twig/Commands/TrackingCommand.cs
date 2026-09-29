using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements the tracking sub-commands under <c>twig workspace</c>:
/// <c>track &lt;id&gt;</c>, <c>track-tree &lt;id&gt;</c>, and <c>untrack &lt;id&gt;</c>.
/// Delegates to <see cref="IWorkItemRepository"/> and <see cref="PinWorkflow"/> for persistence.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// each outcome emits a per-format record. <see cref="OutputFormatterFactory"/> is retained
/// only for stderr errors.
/// </remarks>
public sealed class TrackingCommand(
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory,
    PinWorkflow pinWorkflow,
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

        // ADO #145: unpinning takes the selector off the CURRENT BENCH. It routes through the
        // mutation-workflow seam, which both surfaces share, so the CLI decides nothing about
        // what a pin means beyond resolving the target and rendering the outcome.
        var outcome = await pinWorkflow.UnpinAsync(id, ct);
        var wasTracked = outcome is PinOutcome.Unpinned { WasPinned: true };
        if (wasTracked)
            RenderOutcome("untracked", $"Untracked #{id}.", id, outputFormat, Severity.Success);
        else
            RenderOutcome("untrackNotTracked", $"#{id} was not tracked.", id, outputFormat, Severity.Info);

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

        // ADO #145: pinning adds a selector to the CURRENT BENCH — an item selector for a single
        // pin, a subtree selector for a tree pin. The subtree is NOT expanded here; it is matched
        // live at evaluation time, which is what makes it pick up children created later.
        await pinWorkflow.PinAsync(id, includeSubtree: mode == TrackingMode.Tree, ct);

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
