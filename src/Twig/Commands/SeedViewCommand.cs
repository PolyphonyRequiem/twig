using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed view</c>: shows a dashboard of all local seeds
/// grouped by their parent work item.
/// </summary>
public sealed class SeedViewCommand(
    IWorkItemRepository workItemRepo,
    IFieldDefinitionStore fieldDefStore,
    ISeedLinkRepository seedLinkRepo,
    TwigConfiguration config,
    RenderingPipelineFactory renderingPipelineFactory,
    RendererFactory rendererFactory)
{
    /// <summary>Display the seed dashboard.</summary>
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var (_, renderer) = renderingPipelineFactory.Resolve(outputFormat);

        // Count writable fields for completeness calculation
        var fieldDefs = await fieldDefStore.GetAllAsync(ct);
        var totalWritableFields = 0;
        foreach (var def in fieldDefs)
        {
            if (!def.IsReadOnly)
                totalWritableFields++;
        }

        var staleDays = config.Seed.StaleDays;

        // Build link map: seed ID → list of links touching that seed
        var linkMap = await BuildLinkMapAsync(ct);

        // TTY human path keeps the rich Spectre.Console layout (Live tables, badges).
        if (renderer is not null)
        {
            await renderer.RenderSeedViewAsync(
                () => BuildGroupsAsync(ct),
                totalWritableFields,
                staleDays,
                ct,
                linkMap);
            return 0;
        }

        var groups = await BuildGroupsAsync(ct);
        var tree = BuildSeedViewTree(groups, totalWritableFields, staleDays, linkMap);
        rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
        return 0;
    }

    private static Twig.RenderTree.RenderTree BuildSeedViewTree(
        IReadOnlyList<SeedViewGroup> groups,
        int totalWritableFields,
        int staleDays,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links)
    {
        var totalSeeds = 0;
        foreach (var g in groups)
            totalSeeds += g.Seeds.Count;

        var groupNodes = new List<RenderNode>(groups.Count);
        foreach (var group in groups)
        {
            var seedNodes = new List<RenderNode>(group.Seeds.Count);
            foreach (var seed in group.Seeds)
            {
                seedNodes.Add(BuildSeedDocument(seed, totalWritableFields, staleDays, links));
            }

            var groupFields = new List<DocumentField>
            {
                new("parent", BuildParentField(group.Parent)),
                new("seeds", new RenderNode.Section(null, seedNodes)),
            };

            var header = group.Parent is not null
                ? $"Parent: #{group.Parent.Id} {group.Parent.Type} — {group.Parent.Title}"
                : "Orphan Seeds";

            groupNodes.Add(new RenderNode.Section(header, [new RenderNode.Document(null, groupFields)]));
        }

        var rootFields = new List<DocumentField>
        {
            new("groups", new RenderNode.Section($"Seeds ({totalSeeds})", groupNodes)),
            new("totalSeeds", new RenderNode.KeyValue("totalSeeds", RenderCell.Integer(totalSeeds))),
        };

        if (totalSeeds == 0)
        {
            rootFields.Insert(0, new DocumentField(
                "empty",
                new RenderNode.Text("No seeds"),
                RenderAudience.HumanOnly));
        }

        return new Twig.RenderTree.RenderTree([new RenderNode.Document(null, rootFields)]);
    }

    private static RenderNode BuildParentField(WorkItem? parent)
    {
        if (parent is null)
            return new RenderNode.KeyValue("parent", new RenderCell(string.Empty, new RenderValue.Null()));

        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(parent.Id),
            ["title"] = RenderCell.String(parent.Title ?? string.Empty),
            ["type"] = RenderCell.String(parent.Type.ToString()),
            ["state"] = RenderCell.String(parent.State ?? string.Empty),
        };
        return new RenderNode.Record("workItem", fields);
    }

    private static RenderNode BuildSeedDocument(
        WorkItem seed,
        int totalWritableFields,
        int staleDays,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links)
    {
        var age = HumanOutputFormatter.FormatSeedAge(seed.SeedCreatedAt);
        var filled = HumanOutputFormatter.CountNonEmptyFields(seed);
        var isStale = HumanOutputFormatter.IsStaleSeed(seed, staleDays);
        var staleMark = isStale ? " ⚠ stale" : string.Empty;
        var summary = $"  #{seed.Id} {seed.Type} {seed.Title} {age} {filled}/{totalWritableFields} fields{staleMark}";

        var fields = new List<DocumentField>
        {
            new("id", new RenderNode.KeyValue("id", RenderCell.Integer(seed.Id))),
            new("title", new RenderNode.KeyValue("title", RenderCell.String(seed.Title ?? string.Empty))),
            new("type", new RenderNode.KeyValue("type", RenderCell.String(seed.Type.ToString()))),
            new("parentId", new RenderNode.KeyValue("parentId", seed.ParentId.HasValue
                ? RenderCell.Integer(seed.ParentId.Value)
                : new RenderCell(string.Empty, new RenderValue.Null()))),
            new("seedCreatedAt", new RenderNode.KeyValue("seedCreatedAt", seed.SeedCreatedAt.HasValue
                ? new RenderCell(
                    seed.SeedCreatedAt.Value.ToString("o"),
                    new RenderValue.String(seed.SeedCreatedAt.Value.ToString("o")))
                : new RenderCell(string.Empty, new RenderValue.Null()))),
            new("age", new RenderNode.KeyValue("age", RenderCell.String(age))),
            new("filledFields", new RenderNode.KeyValue("filledFields", RenderCell.Integer(filled))),
            new("totalWritableFields", new RenderNode.KeyValue("totalWritableFields", RenderCell.Integer(totalWritableFields))),
            new("isStale", new RenderNode.KeyValue("isStale", RenderCell.Boolean(isStale))),
        };

        // Per-seed link list — empty array preserves the original wire shape.
        var linkNodes = new List<RenderNode>();
        if (links is not null && links.TryGetValue(seed.Id, out var seedLinks))
        {
            foreach (var link in seedLinks)
            {
                var annotation = HumanOutputFormatter.FormatLinkAnnotation(seed.Id, link);
                var linkFields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["sourceId"] = RenderCell.Integer(link.SourceId),
                    ["targetId"] = RenderCell.Integer(link.TargetId),
                    ["linkType"] = RenderCell.String(link.LinkType ?? string.Empty),
                    ["annotation"] = new RenderCell($"→ {annotation}", new RenderValue.String(annotation)),
                };
                linkNodes.Add(new RenderNode.Record(null, linkFields));
            }
        }
        fields.Add(new DocumentField("links", new RenderNode.Section(null, linkNodes)));

        // Human-only one-liner summary that flows in the Section header layout.
        fields.Insert(0, new DocumentField(
            "summary",
            new RenderNode.Text(summary),
            RenderAudience.HumanOnly));

        return new RenderNode.Document(null, fields);
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>?> BuildLinkMapAsync(CancellationToken ct)
    {
        var allLinks = await seedLinkRepo.GetAllSeedLinksAsync(ct);
        if (allLinks.Count == 0)
            return null;

        var map = new Dictionary<int, List<SeedLink>>();

        foreach (var link in allLinks)
        {
            GetOrAdd(link.SourceId).Add(link);
            GetOrAdd(link.TargetId).Add(link);
        }

        var result = new Dictionary<int, IReadOnlyList<SeedLink>>(map.Count);
        foreach (var kvp in map)
            result[kvp.Key] = kvp.Value;
        return result;

        List<SeedLink> GetOrAdd(int id)
        {
            if (!map.TryGetValue(id, out var list))
                map[id] = list = [];
            return list;
        }
    }

    private async Task<IReadOnlyList<SeedViewGroup>> BuildGroupsAsync(CancellationToken ct)
    {
        var seeds = await workItemRepo.GetSeedsAsync(ct);
        if (seeds.Count == 0)
            return Array.Empty<SeedViewGroup>();

        // Group seeds by ParentId — use string key to avoid nullable TKey constraint
        var parentedGroups = new Dictionary<int, List<WorkItem>>();
        List<WorkItem>? orphans = null;

        foreach (var seed in seeds)
        {
            if (seed.ParentId is null)
            {
                orphans ??= new List<WorkItem>();
                orphans.Add(seed);
            }
            else
            {
                if (!parentedGroups.TryGetValue(seed.ParentId.Value, out var list))
                {
                    list = new List<WorkItem>();
                    parentedGroups[seed.ParentId.Value] = list;
                }
                list.Add(seed);
            }
        }

        var result = new List<SeedViewGroup>();

        // Parented groups first
        foreach (var kvp in parentedGroups)
        {
            WorkItem? parent = null;
            try
            {
                parent = await workItemRepo.GetByIdAsync(kvp.Key, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation
            }
            catch (Exception)
            {
                // Parent not in cache — proceed without metadata
            }

            result.Add(new SeedViewGroup(parent, kvp.Value));
        }

        // Orphan seeds last
        if (orphans is not null)
        {
            result.Add(new SeedViewGroup(null, orphans));
        }

        return result;
    }
}
