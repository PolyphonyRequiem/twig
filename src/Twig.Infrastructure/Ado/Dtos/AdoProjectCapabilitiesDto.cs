using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// DTO for GET /_apis/projects/{project}?includeCapabilities=true&amp;api-version=7.1
/// Maps the <c>capabilities.processTemplate.templateName</c> path from the ADO Projects API.
/// </summary>
internal sealed class AdoProjectWithCapabilitiesResponse
{
    [JsonPropertyName("capabilities")]
    public AdoProjectCapabilities? Capabilities { get; set; }
}

internal sealed class AdoProjectCapabilities
{
    [JsonPropertyName("processTemplate")]
    public AdoProcessTemplate? ProcessTemplate { get; set; }
}

internal sealed class AdoProcessTemplate
{
    [JsonPropertyName("templateName")]
    public string? TemplateName { get; set; }
}
