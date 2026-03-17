using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// ADO REST API response for a single pull request.
/// </summary>
internal sealed class AdoPullRequestResponse
{
    [JsonPropertyName("pullRequestId")]
    public int PullRequestId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("sourceRefName")]
    public string SourceRefName { get; set; } = string.Empty;

    [JsonPropertyName("targetRefName")]
    public string TargetRefName { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("repository")]
    public AdoPullRequestRepositoryRef? Repository { get; set; }
}

/// <summary>
/// Repository reference embedded in PR responses.
/// </summary>
internal sealed class AdoPullRequestRepositoryRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// ADO REST API response for a list of pull requests.
/// </summary>
internal sealed class AdoPullRequestListResponse
{
    [JsonPropertyName("value")]
    public List<AdoPullRequestResponse> Value { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// ADO REST API response for a repository.
/// </summary>
internal sealed class AdoRepositoryResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("project")]
    public AdoProjectRef? Project { get; set; }
}

/// <summary>
/// Project reference embedded in repository responses.
/// </summary>
internal sealed class AdoProjectRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// ADO REST API response for a project.
/// </summary>
internal sealed class AdoProjectResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Request body for creating a pull request.
/// </summary>
internal sealed class AdoCreatePullRequestRequest
{
    [JsonPropertyName("sourceRefName")]
    public string SourceRefName { get; set; } = string.Empty;

    [JsonPropertyName("targetRefName")]
    public string TargetRefName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("workItemRefs")]
    public List<AdoWorkItemRef>? WorkItemRefs { get; set; }
}

/// <summary>
/// Work item reference for PR creation.
/// </summary>
internal sealed class AdoWorkItemRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// Typed DTO for artifact link relation body used in AddArtifactLinkAsync.
/// Avoids Dictionary&lt;string, object?&gt; polymorphism for AOT serialization.
/// </summary>
internal sealed class AdoArtifactLinkRelation
{
    [JsonPropertyName("rel")]
    public string Rel { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public AdoArtifactLinkAttributes Attributes { get; set; } = new();
}

/// <summary>
/// Attributes for an artifact link relation.
/// </summary>
internal sealed class AdoArtifactLinkAttributes
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
