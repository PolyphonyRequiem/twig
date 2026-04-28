using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed chain</c>: creates a chain of seeds with auto-linking.
/// Supports batch mode (explicit titles) or interactive mode (prompt loop).
/// Each seed is linked to the previous one with "successor" type.
/// Supports <c>--parent &lt;id&gt;</c> to override active context and <c>--type &lt;type&gt;</c>
/// to set work item type for all chain seeds.
/// </summary>
public sealed class SeedChainCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    SeedFactory seedFactory,
    ISeedIdCounter seedIdCounter)
{
    /// <summary>Create a chain of seeds interactively or from explicit titles, linking each to the previous.</summary>
    public async Task<int> ExecuteAsync(
        int? parentOverride,
        string? type,
        string outputFormat,
        CancellationToken ct,
        string[]? titles = null)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Resolve parent context
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

        // 2. Determine work item type
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

        // 3. Initialize seed counter from DB
        var minSeedId = await workItemRepo.GetMinSeedIdAsync(ct);
        if (minSeedId.HasValue)
            seedIdCounter.Initialize(minSeedId.Value);

        // 4. Create seeds — batch mode (explicit titles) or interactive loop
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

            Console.WriteLine(fmt.FormatInfo($"  #{seed.Id} {seed.Title}"));
            createdSeeds.Add(seed);
        }

        // 5. Summary
        if (createdSeeds.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No seeds created."));
            return 0;
        }

        var idChain = string.Join(" \u2192 ", createdSeeds.Select(s => $"#{s.Id}"));
        Console.WriteLine(fmt.FormatSuccess($"Created {createdSeeds.Count} seeds: {idChain}"));

        var hints = hintEngine.GetHints("seed-chain", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}
