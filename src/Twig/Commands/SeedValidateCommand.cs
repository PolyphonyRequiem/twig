using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed validate [id]</c>: validates seeds against publish rules.
/// With an ID, validates a single seed with detailed output.
/// Without an ID, validates all seeds with a summary.
/// </summary>
public sealed class SeedValidateCommand(
    IWorkItemRepository workItemRepo,
    ISeedPublishRulesProvider rulesProvider,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Validate one or all seeds against publish rules.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var rules = await rulesProvider.GetRulesAsync(ct);

        if (id.HasValue)
        {
            var seed = await workItemRepo.GetByIdAsync(id.Value, ct);
            if (seed is null || !seed.IsSeed)
            {
                Console.Error.WriteLine(fmt.FormatError($"Seed #{id.Value} not found."));
                return 1;
            }

            var result = SeedValidator.Validate(seed, rules);
            Console.WriteLine(fmt.FormatSeedValidation([result]));
            return result.Passed ? 0 : 1;
        }

        var seeds = await workItemRepo.GetSeedsAsync(ct);
        if (seeds.Count == 0)
        {
            Console.WriteLine(fmt.FormatSeedValidation([]));
            return 0;
        }

        var results = new List<SeedValidationResult>(seeds.Count);
        foreach (var seed in seeds)
        {
            results.Add(SeedValidator.Validate(seed, rules));
        }

        Console.WriteLine(fmt.FormatSeedValidation(results));
        return results.Exists(r => !r.Passed) ? 1 : 0;
    }
}
