using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for team field values (area paths).
/// Endpoint: GET {org}/{project}/{team}/_apis/work/teamsettings/teamfieldvalues
/// </summary>
internal sealed class AdoTeamFieldValuesResponse
{
    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("values")]
    public List<AdoTeamFieldValueDto>? Values { get; set; }
}

/// <summary>
/// A single team field value entry — represents an area path the team owns.
/// </summary>
internal sealed class AdoTeamFieldValueDto
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("includeChildren")]
    public bool IncludeChildren { get; set; }
}
