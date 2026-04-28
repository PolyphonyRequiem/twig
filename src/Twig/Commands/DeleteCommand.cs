using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig delete &lt;id&gt;</c>: permanently deletes a single ADO work item
/// with multiple safety guards (seed check, link check, interactive confirmation).
/// Requires an explicit ID — does NOT default to the active work item.
/// </summary>
public sealed class DeleteCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    CommandContext ctx,
    IPromptStateWriter? promptStateWriter = null)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;

    /// <summary>Permanently delete a work item from Azure DevOps.</summary>
    /// <param name="id">The work item ID to delete (required).</param>
    /// <param name="force">Skip the interactive confirmation prompt.</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    public async Task<int> ExecuteAsync(
        int id,
        bool force = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        int exitCode;

        try
        {
            exitCode = await ExecuteCoreAsync(id, force, outputFormat, fmt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(fmt.FormatError($"Delete failed: {ex.Message}"));
            exitCode = 1;
        }

        TelemetryHelper.TrackCommand(
            ctx.TelemetryClient,
            "delete",
            outputFormat,
            exitCode,
            startTimestamp,
            extraProperties: new Dictionary<string, string>
            {
                ["used_force"] = force.ToString(),
            });

        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(
        int id,
        bool force,
        string outputFormat,
        IOutputFormatter fmt,
        CancellationToken ct)
    {
        // 1. Resolve item from cache or ADO
        var resolved = await activeItemResolver.ResolveByIdAsync(id, ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found. Consider 'twig state Closed' for items you no longer need."
                : $"Work item #{id} could not be resolved: {errorReason}"));
            return 1;
        }

        // 2. Seed guard
        if (item.IsSeed)
        {
            _stderr.WriteLine(fmt.FormatError($"#{id} is a seed. Use 'twig seed discard {id}' instead."));
            return 1;
        }

        // 3. Fresh fetch with links from ADO (not cache)
        var (freshItem, links) = await adoService.FetchWithLinksAsync(id, ct);

        // 4. Children guard
        var children = await adoService.FetchChildrenAsync(id, ct);

        // 5. Link guard — refuse if any links exist
        var linkSummary = BuildLinkSummary(freshItem.ParentId, children.Count, links);
        if (linkSummary.TotalCount > 0)
        {
            _stderr.WriteLine(fmt.FormatError(
                $"Cannot delete #{id} '{freshItem.Title}' — it has {linkSummary.TotalCount} link(s): {linkSummary.Description}. " +
                "Remove all links before deleting. Consider 'twig state Closed' instead — it preserves history and is reversible."));
            return 1;
        }

        // 6. Confirmation prompt
        if (!force)
        {
            if (consoleInput.IsOutputRedirected)
            {
                _stderr.WriteLine(fmt.FormatError(
                    "Cannot confirm deletion in non-interactive mode. Use --force to bypass confirmation."));
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"  ID:    #{freshItem.Id}");
            Console.WriteLine($"  Title: {freshItem.Title}");
            Console.WriteLine($"  Type:  {freshItem.Type}");
            Console.WriteLine($"  State: {freshItem.State}");
            Console.WriteLine();
            Console.WriteLine("⚠ This action is PERMANENT. Consider 'twig state Closed' instead — it preserves history and is reversible.");
            Console.WriteLine();
            Console.Write("Type 'yes' to confirm deletion: ");

            var response = consoleInput.ReadLine();
            if (!string.Equals(response?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(fmt.FormatInfo("Delete cancelled."));
                return 0;
            }
        }

        // 7. Audit trail — best-effort note on parent
        if (freshItem.ParentId.HasValue)
        {
            try
            {
                await adoService.AddCommentAsync(
                    freshItem.ParentId.Value,
                    $"Child work item #{id} '{freshItem.Title}' ({freshItem.Type}) was deleted via twig.",
                    ct);
            }
            catch
            {
                // Best-effort — parent may be inaccessible or deleted
            }
        }

        // 8. Delete from ADO
        await adoService.DeleteAsync(id, ct);

        // 9. Cache cleanup
        await workItemRepo.DeleteByIdAsync(id, ct);
        await linkRepo.SaveLinksAsync(id, Array.Empty<WorkItemLink>(), ct);
        await pendingChangeStore.ClearChangesAsync(id, ct);

        // 10. Prompt state refresh
        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        // 11. Output
        Console.WriteLine(fmt.FormatSuccess($"Deleted #{id} '{freshItem.Title}'."));

        var hints = ctx.HintEngine.GetHints("delete", item: freshItem, outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    // ── Link summary helpers ────────────────────────────────────────

    private static LinkSummaryResult BuildLinkSummary(
        int? parentId,
        int childCount,
        IReadOnlyList<WorkItemLink> nonHierarchyLinks)
    {
        var parts = new List<string>();
        var total = 0;

        if (parentId.HasValue)
        {
            parts.Add("1 parent");
            total++;
        }

        if (childCount > 0)
        {
            parts.Add($"{childCount} child{(childCount != 1 ? "ren" : "")}");
            total += childCount;
        }

        // Group non-hierarchy links by type
        var linksByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in nonHierarchyLinks)
        {
            linksByType.TryGetValue(link.LinkType, out var count);
            linksByType[link.LinkType] = count + 1;
        }

        foreach (var (linkType, count) in linksByType)
        {
            parts.Add($"{count} {linkType.ToLowerInvariant()}");
            total += count;
        }

        return new LinkSummaryResult(total, string.Join(", ", parts));
    }

    private readonly record struct LinkSummaryResult(int TotalCount, string Description);
}
