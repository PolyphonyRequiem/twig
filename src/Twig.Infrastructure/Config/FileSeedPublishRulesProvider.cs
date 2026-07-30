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
            var text = await File.ReadAllTextAsync(_path, ct);
            var rules = JsonSerializer.Deserialize(text, TwigJsonContext.Default.SeedPublishRules);
            if (rules is null)
            {
                return SeedPublishRules.Default;
            }

            // Whether STJ source-gen leaves an omitted property null or preserves its property
            // initializer changed between SDK 11.0.100-preview.3 and preview.5, so neither a
            // null check nor an empty check can distinguish "omitted" from "explicitly set".
            // Ask the document which keys were actually present instead.
            return new SeedPublishRules
            {
                RequiredFields = HasProperty(text, "requiredFields")
                    ? rules.RequiredFields ?? []
                    : SeedPublishRules.Default.RequiredFields,
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

    /// <summary>
    /// Returns true when <paramref name="name"/> is present as a top-level key in the document,
    /// regardless of its value. Used to tell an omitted property from an explicitly set one.
    /// </summary>
    private static bool HasProperty(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(name, out _);
    }
}
