using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig web</c>: opens the active work item in Azure DevOps in the default browser.
/// </summary>
public sealed class WebCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory)
{
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

        // Seeds (negative IDs) don't have an ADO URL
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
        Console.WriteLine(fmt.FormatInfo($"Opened {label} in browser."));
        return 0;
    }
}
