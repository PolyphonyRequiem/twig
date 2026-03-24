using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Loads <see cref="SeedPublishRules"/> from <c>.twig/seed-rules.json</c>.
/// Returns <see cref="SeedPublishRules.Default"/> when the file does not exist.
/// Throws <see cref="TwigConfigurationException"/> on malformed JSON.
/// </summary>
internal sealed class FileSeedPublishRulesProvider : ISeedPublishRulesProvider
{
    private readonly string _path;

    public FileSeedPublishRulesProvider(string twigDir)
    {
        _path = Path.Combine(twigDir, "seed-rules.json");
    }

    public async Task<SeedPublishRules> GetRulesAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return SeedPublishRules.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var rules = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.SeedPublishRules, ct);
            if (rules is null)
            {
                return SeedPublishRules.Default;
            }

            // STJ source-gen does not preserve init defaults for omitted fields — merge them explicitly.
            return new SeedPublishRules
            {
                RequiredFields = rules.RequiredFields ?? SeedPublishRules.Default.RequiredFields,
                RequireParent = rules.RequireParent,
            };
        }
        catch (JsonException ex)
        {
            throw new TwigConfigurationException(
                $"Seed rules file '{_path}' contains invalid JSON. Delete the file or fix the syntax. Details: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new TwigConfigurationException(
                $"Cannot read seed rules file '{_path}': {ex.Message}", ex);
        }
    }
}
