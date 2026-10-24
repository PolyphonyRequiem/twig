using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.Infrastructure.GitHub;

internal sealed record GitHubIssueGraphQlRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] Dictionary<string, string?> Variables);

internal sealed record GitHubIssueGraphQlResponse
{
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("errors")]
    public JsonElement Errors { get; init; }
}

internal sealed record GitHubIssueAssignee(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("login")] string Login);
