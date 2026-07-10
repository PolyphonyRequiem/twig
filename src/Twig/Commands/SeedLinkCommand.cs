using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Handles <c>twig seed link</c>, <c>seed unlink</c>, and <c>seed links</c> commands.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// success messages emit per-format Records; the links list emits a Document with a
/// links Table on machine formats and streamed lines on human format.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class SeedLinkCommand(
    ISeedLinkRepository seedLinkRepo,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Create a virtual link between two items.</summary>
    public async Task<int> LinkAsync(
        int sourceId,
        int targetId,
        string? type,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (sourceId >= 0 && targetId >= 0)
        {
            Console.Error.WriteLine(fmt.FormatError(
                "At least one ID must be a seed (negative). Use ADO for linking positive work items."));
            return 1;
        }

        var rawType = type ?? SeedLinkTypes.Related;
        var linkType = NormalizeLinkType(rawType);
        if (linkType is null)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Invalid link type '{rawType}'. Valid types: {string.Join(", ", SeedLinkTypes.All)}"));
            return 1;
        }

        if (linkType == SeedLinkTypes.ParentChild && sourceId < 0)
        {
            if (sourceId == targetId)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Link rejected: seed #{sourceId} cannot be its own parent."));
                return 1;
            }

            var childSeed = await workItemRepo.GetByIdAsync(sourceId, ct);
            if (childSeed is null || !childSeed.IsSeed)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Seed #{sourceId} not found."));
                return 1;
            }

            if (childSeed.ParentId.HasValue && childSeed.ParentId.Value != targetId)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Seed #{sourceId} already has parent #{childSeed.ParentId.Value}. Remove that parent link first."));
                return 1;
            }

            var existingParentId = (await seedLinkRepo.GetLinksForItemAsync(sourceId, ct))
                .Where(link =>
                    link.LinkType == SeedLinkTypes.ParentChild &&
                    link.SourceId == sourceId &&
                    link.TargetId != targetId)
                .Select(link => (int?)link.TargetId)
                .FirstOrDefault();
            if (existingParentId.HasValue)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Seed #{sourceId} already has parent #{existingParentId.Value}. Remove that parent link first."));
                return 1;
            }

            if (childSeed.ParentId != targetId)
                await workItemRepo.SaveAsync(childSeed.WithParentId(targetId), ct);
        }

        if (sourceId > 0 && !await workItemRepo.ExistsByIdAsync(sourceId, ct))
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Warning: work item #{sourceId} is not in the local cache. Link created anyway."));
        }
        if (targetId > 0 && !await workItemRepo.ExistsByIdAsync(targetId, ct))
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Warning: work item #{targetId} is not in the local cache. Link created anyway."));
        }

        if (linkType != SeedLinkTypes.Related && linkType != SeedLinkTypes.ParentChild)
        {
            if (sourceId == targetId)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Link rejected: would create a dependency cycle involving seeds: #{sourceId}"));
                return 1;
            }

            var seeds = await workItemRepo.GetSeedsAsync(ct);
            var existingLinks = await seedLinkRepo.GetAllSeedLinksAsync(ct);
            var proposed = new SeedLink(sourceId, targetId, linkType, DateTimeOffset.UtcNow);

            if (SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed))
            {
                var allLinks = existingLinks.Append(proposed).ToList();
                var (_, cyclicIds) = SeedDependencyGraph.Sort(seeds, allLinks);
                var idList = string.Join(", ", cyclicIds.OrderBy(id => id).Select(id => $"#{id}"));
                Console.Error.WriteLine(fmt.FormatError(
                    $"Link rejected: would create a dependency cycle involving seeds: {idList}"));
                return 1;
            }
        }

        try
        {
            await seedLinkRepo.AddLinkAsync(new SeedLink(sourceId, targetId, linkType, DateTimeOffset.UtcNow), ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"A '{linkType}' link from #{sourceId} to #{targetId} already exists."));
            return 1;
        }

        RenderLinkOutcome("seedLinked", $"Linked #{sourceId} ──{linkType}──▶ #{targetId}", sourceId, targetId, linkType, outputFormat);
        return 0;
    }

    /// <summary>Remove a virtual link.</summary>
    public async Task<int> UnlinkAsync(
        int sourceId,
        int targetId,
        string? type,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var rawType = type ?? SeedLinkTypes.Related;
        var linkType = NormalizeLinkType(rawType);
        if (linkType is null)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Invalid link type '{rawType}'. Valid types: {string.Join(", ", SeedLinkTypes.All)}"));
            return 1;
        }

        await seedLinkRepo.RemoveLinkAsync(sourceId, targetId, linkType, ct);

        if (linkType == SeedLinkTypes.ParentChild && sourceId < 0)
        {
            var childSeed = await workItemRepo.GetByIdAsync(sourceId, ct);
            if (childSeed is not null && childSeed.IsSeed && childSeed.ParentId == targetId)
                await workItemRepo.SaveAsync(childSeed.WithParentId(null), ct);
        }

        RenderLinkOutcome("seedUnlinked", $"Unlinked #{sourceId} ──{linkType}──▶ #{targetId}", sourceId, targetId, linkType, outputFormat);
        return 0;
    }

    private static string? NormalizeLinkType(string type)
    {
        foreach (var t in SeedLinkTypes.All)
            if (string.Equals(t, type, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    /// <summary>List virtual links for an item, or all links.</summary>
    public async Task<int> ListLinksAsync(
        int? id,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var links = id.HasValue
            ? await seedLinkRepo.GetLinksForItemAsync(id.Value, ct)
            : await seedLinkRepo.GetAllSeedLinksAsync(ct);

        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();

        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var columns = new List<RenderColumn>
            {
                new("sourceId", "Source"),
                new("targetId", "Target"),
                new("linkType", "Type"),
                new("createdAt", "Created"),
            };
            var rows = new List<RenderRow>(links.Count);
            foreach (var link in links)
            {
                rows.Add(new RenderRow("seedLink", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["sourceId"] = RenderCell.Integer(link.SourceId),
                    ["targetId"] = RenderCell.Integer(link.TargetId),
                    ["linkType"] = RenderCell.String(link.LinkType),
                    ["createdAt"] = RenderCell.String(link.CreatedAt.ToString("o")),
                }));
            }
            var fields = new List<DocumentField>(2)
            {
                new("links", new RenderNode.Table(null, columns, rows)),
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(links.Count))),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Document("seedLinks", fields),
            }));
            return 0;
        }

        if (links.Count == 0)
        {
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Text(id.HasValue ? $"No links for #{id.Value}." : "No seed links.", Severity.Info),
            }));
            return 0;
        }

        var nodes = new List<RenderNode>(links.Count + 1);
        foreach (var link in links)
            nodes.Add(new RenderNode.Text($"#{link.SourceId} ──{link.LinkType}──▶ #{link.TargetId}", Severity.Info));
        nodes.Add(new RenderNode.Text($"{links.Count} link(s) total.", Severity.Info));
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(nodes));
        return 0;
    }

    private void RenderLinkOutcome(string kind, string message, int sourceId, int targetId, string linkType, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" => BuildLinkRecord(kind, message, sourceId, targetId, linkType),
            _ => new RenderNode.Text(message, Severity.Success),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static RenderNode BuildLinkRecord(string kind, string message, int sourceId, int targetId, string linkType)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["sourceId"] = RenderCell.Integer(sourceId),
            ["targetId"] = RenderCell.Integer(targetId),
            ["linkType"] = RenderCell.String(linkType),
            ["message"] = RenderCell.String(message),
        };
        return new RenderNode.Record(kind, fields);
    }
}
