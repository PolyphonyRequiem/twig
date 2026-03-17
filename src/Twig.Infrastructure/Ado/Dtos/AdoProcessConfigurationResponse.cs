using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// DTO for GET /{project}/_apis/work/processconfiguration?api-version=7.1
/// Internal to Twig.Infrastructure — callers receive <c>ProcessConfigurationData</c> (domain DTO) instead.
/// </summary>
internal sealed class AdoProcessConfigurationResponse
{
    [JsonPropertyName("taskBacklog")]
    public AdoCategoryConfiguration? TaskBacklog { get; set; }

    [JsonPropertyName("requirementBacklog")]
    public AdoCategoryConfiguration? RequirementBacklog { get; set; }

    [JsonPropertyName("portfolioBacklogs")]
    public List<AdoCategoryConfiguration>? PortfolioBacklogs { get; set; }

    [JsonPropertyName("bugWorkItems")]
    public AdoCategoryConfiguration? BugWorkItems { get; set; }
}

/// <summary>
/// A single backlog category configuration (taskBacklog, requirementBacklog, a portfolio backlog, or bugWorkItems).
/// </summary>
internal sealed class AdoCategoryConfiguration
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    [JsonPropertyName("workItemTypes")]
    public List<AdoWorkItemTypeRef>? WorkItemTypes { get; set; }
}

/// <summary>
/// Minimal work item type reference within a backlog category.
/// </summary>
internal sealed class AdoWorkItemTypeRef
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
