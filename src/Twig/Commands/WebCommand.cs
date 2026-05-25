using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig web</c>: opens the active work item in Azure DevOps in the default browser.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// emits a "browserOpened" record after launching the browser.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class WebCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Open the active (or specified) work item in the browser.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        int targetId;
        if (id.HasValue)
        {
            targetId = id.Value;
        }
        else
        {
            var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
            if (activeId is null)
            {
                Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
                return 1;
            }
            targetId = activeId.Value;
        }

        if (targetId < 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"#{targetId} is a local seed — publish it first to open in browser."));
            return 1;
        }

        if (string.IsNullOrWhiteSpace(config.Organization) || string.IsNullOrWhiteSpace(config.Project))
        {
            Console.Error.WriteLine(fmt.FormatError("Organization or project not configured. Run 'twig init' first."));
            return 1;
        }

        var url = $"https://dev.azure.com/{Uri.EscapeDataString(config.Organization)}/{Uri.EscapeDataString(config.Project)}/_workitems/edit/{targetId}";

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var item = await workItemRepo.GetByIdAsync(targetId, ct);
        var label = item is not null ? $"#{targetId} {item.Title}" : $"#{targetId}";
        var message = $"Opened {label} in browser.";

        var tree = BuildTree(targetId, item?.Title, url, message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        return 0;
    }

    private static RenderTree.RenderTree BuildTree(int itemId, string? title, string url, string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildRecord(itemId, title, url, message),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildRecord(int itemId, string? title, string url, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["itemId"] = RenderCell.Integer(itemId),
            ["url"] = RenderCell.String(url),
            ["message"] = RenderCell.String(message),
        };
        if (!string.IsNullOrEmpty(title))
            fields["title"] = RenderCell.String(title);
        return new RenderNode.Record("browserOpened", fields);
    }
}
