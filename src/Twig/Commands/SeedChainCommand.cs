using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed chain</c>: interactive loop for rapid sequential seed creation
/// with auto-linking. Each seed is linked to the previous one with "related" type.
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
    HintEngine hintEngine)
{
    /// <summary>Create a chain of seeds interactively, linking each to the previous.</summary>
    public async Task<int> ExecuteAsync(
        int? parentOverride,
        string? type,
        string outputFormat,
        CancellationToken ct)
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
            WorkItem.InitializeSeedCounter(minSeedId.Value);

        // 4. Interactive loop
        var createdSeeds = new List<WorkItem>();
        var suppressPrompt = consoleInput.IsOutputRedirected;

        while (true)
        {
            if (!suppressPrompt)
                Console.Write("Seed title (empty to finish): ");

            var line = consoleInput.ReadLine();
            if (string.IsNullOrEmpty(line))
                break;

            var seedResult = SeedFactory.Create(line, parent, processConfig, typeOverride);
            if (!seedResult.IsSuccess)
            {
                // Print partial summary so the user knows what was already persisted
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

            // Link to previous seed in chain (related)
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
