using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed chain</c>: creates a chain of seeds with auto-linking.
/// Supports batch mode (explicit titles) or interactive mode (prompt loop).
/// Each seed is linked to the previous one with "successor" type.
/// Supports <c>--parent &lt;id&gt;</c> to override active context and <c>--type &lt;type&gt;</c>
/// to set work item type for all chain seeds.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// on human format, per-seed creation prints individual Info lines as before; the
/// final summary emits "seedChainCreated" via the renderer. On machine formats
/// (json/json-*) per-seed prints are suppressed and a single "seedChainCreated"
/// Document with a ``seeds`` Table is emitted containing the full chain.
/// "No seeds created" emits a "seedChainEmpty" record. Hints remain human-only
/// via the legacy formatter. <see cref="OutputFormatterFactory"/> is retained for
/// stderr errors and hint formatting.
/// </remarks>
public sealed class SeedChainCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    SeedFactory seedFactory,
    ISeedIdCounter seedIdCounter,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Create a chain of seeds interactively or from explicit titles, linking each to the previous.</summary>
    public async Task<int> ExecuteAsync(
        int? parentOverride,
        string? type,
        string outputFormat,
        CancellationToken ct,
        string[]? titles = null)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";

        var resolved = parentOverride.HasValue
            ? await activeItemResolver.ResolveByIdAsync(parentOverride.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);

        if (!resolved.TryGetWorkItem(out var parent, out var errorId, out var errorReason))
        {
            var message = errorId is not null
                ? $"Work item #{errorId} is unreachable: {errorReason}"
                : parentOverride.HasValue
                    ? $"Work item #{parentOverride.Value} not found."
                    : "No active context. Use --parent <id> or 'twig set <id>' first.";
            Console.Error.WriteLine(fmt.FormatError(message));
            return 1;
        }

        var processConfig = processConfigProvider.GetConfiguration();
        WorkItemType? typeOverride = null;
        if (type is not null)
        {
            var typeResult = WorkItemType.Parse(type);
            if (!typeResult.IsSuccess)
            {
                Console.Error.WriteLine(fmt.FormatError(typeResult.Error));
                return 1;
            }
            typeOverride = typeResult.Value;
        }

        var minSeedId = await workItemRepo.GetMinSeedIdAsync(ct);
        if (minSeedId.HasValue)
            seedIdCounter.Initialize(minSeedId.Value);

        var createdSeeds = new List<WorkItem>();

        IEnumerable<string> GetTitleSource()
        {
            if (titles is { Length: > 0 })
            {
                foreach (var t in titles) yield return t;
                yield break;
            }
            var suppressPrompt = consoleInput.IsOutputRedirected;
            while (true)
            {
                if (!suppressPrompt)
                    Console.Write("Seed title (empty to finish): ");
                var line = consoleInput.ReadLine();
                if (string.IsNullOrEmpty(line))
                    yield break;
                yield return line;
            }
        }

        foreach (var title in GetTitleSource())
        {
            var seedResult = seedFactory.Create(title, parent, processConfig, typeOverride);
            if (!seedResult.IsSuccess)
            {
                if (createdSeeds.Count > 0)
                {
                    var partialChain = string.Join(" \u2192 ", createdSeeds.Select(s => $"#{s.Id}"));
                    Console.Error.WriteLine(fmt.FormatInfo(
                        $"Created {createdSeeds.Count} seeds before error: {partialChain}"));
                }
                Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
                return 1;
            }

            var seed = seedResult.Value;
            await workItemRepo.SaveAsync(seed, ct);

            if (createdSeeds.Count > 0)
            {
                var previousSeed = createdSeeds[^1];
                await seedLinkRepo.AddLinkAsync(
                    new SeedLink(previousSeed.Id, seed.Id, SeedLinkTypes.Successor, DateTimeOffset.UtcNow), ct);
            }

            if (!isMachine)
                Console.WriteLine($"  #{seed.Id} {seed.Title}");
            createdSeeds.Add(seed);
        }

        if (createdSeeds.Count == 0)
        {
            const string emptyMessage = "No seeds created.";
            RenderNode emptyNode = lower switch
            {
                "minimal" => new RenderNode.Text(emptyMessage),
                "json" or "json-full" or "json-compact" or "ids" =>
                    new RenderNode.Record("seedChainEmpty", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["message"] = RenderCell.String(emptyMessage),
                    }),
                _ => new RenderNode.Text(emptyMessage, Severity.Info),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { emptyNode }));
            return 0;
        }

        var idChain = string.Join(" \u2192 ", createdSeeds.Select(s => $"#{s.Id}"));
        var summaryMessage = $"Created {createdSeeds.Count} seeds: {idChain}";

        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var columns = new List<RenderColumn>
            {
                new("id", "ID"),
                new("title", "Title"),
            };
            var rows = new List<RenderRow>(createdSeeds.Count);
            foreach (var s in createdSeeds)
            {
                rows.Add(new RenderRow("seed", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["id"] = RenderCell.Integer(s.Id),
                    ["title"] = RenderCell.String(s.Title),
                }));
            }
            var fields = new List<DocumentField>(3)
            {
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(createdSeeds.Count))),
                new("chain", new RenderNode.KeyValue("chain", RenderCell.String(idChain))),
                new("seeds", new RenderNode.Table(null, columns, rows)),
            };
            var doc = new RenderNode.Document("seedChainCreated", fields);
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { (RenderNode)doc }));
        }
        else
        {
            RenderNode summaryNode = lower == "minimal"
                ? new RenderNode.Text(summaryMessage)
                : new RenderNode.Text(summaryMessage, Severity.Success);
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { summaryNode }));
        }

        var hints = hintEngine.GetHints("seed-chain", outputFormat: outputFormat ?? "human");
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}