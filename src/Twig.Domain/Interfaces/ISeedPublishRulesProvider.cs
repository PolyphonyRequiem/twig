using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Provides the publish rules that seeds are validated against before publishing to ADO.
/// </summary>
public interface ISeedPublishRulesProvider
{
    Task<SeedPublishRules> GetRulesAsync(CancellationToken ct = default);
}
