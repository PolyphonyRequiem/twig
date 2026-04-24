using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements the five tracking sub-commands under <c>twig workspace</c>:
/// <c>track &lt;id&gt;</c>, <c>track-tree &lt;id&gt;</c>, <c>untrack &lt;id&gt;</c>,
/// <c>exclude &lt;id&gt;</c>, and <c>exclusions</c>.
/// Delegates to <see cref="ITrackingService"/> for persistence.
/// </summary>
public sealed class TrackingCommand(
    ITrackingService trackingService,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory)
{
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
            Console.WriteLine(fmt.FormatSuccess($"Untracked #{id}."));
        else
            Console.WriteLine(fmt.FormatInfo($"#{id} was not tracked."));

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
        Console.WriteLine(fmt.FormatSuccess($"Excluded #{id} from workspace view."));
        return 0;
    }

    /// <summary>List all exclusions, or clear/remove exclusions.</summary>
    public async Task<int> ExclusionsAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool clear = false, int? remove = null, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (clear)
        {
            var count = await trackingService.ClearExclusionsAsync(ct);
            Console.WriteLine(count > 0
                ? fmt.FormatSuccess($"Cleared {count} exclusion(s).")
                : fmt.FormatInfo("No exclusions to clear."));
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
            Console.WriteLine(wasExcluded
                ? fmt.FormatSuccess($"Removed exclusion for #{removeId}.")
                : fmt.FormatInfo($"#{removeId} was not excluded."));
            return 0;
        }

        var exclusions = await trackingService.ListExclusionsAsync(ct);

        if (exclusions.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No exclusions configured."));
            return 0;
        }

        foreach (var item in exclusions)
        {
            var title = await GetTitleAsync(item.WorkItemId, ct);
            var display = title is not null
                ? $"#{item.WorkItemId}: {title}"
                : $"#{item.WorkItemId}";
            Console.WriteLine(fmt.FormatInfo(display));
        }

        Console.WriteLine(fmt.FormatInfo($"{exclusions.Count} exclusion(s) total."));
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
        Console.WriteLine(fmt.FormatSuccess(display));
        return 0;
    }

    private async Task<string?> GetTitleAsync(int id, CancellationToken ct)
    {
        var item = await workItemRepo.GetByIdAsync(id, ct);
        return item?.Title;
    }
}
