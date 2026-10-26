using System.Text;
using System.Text.Json;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

internal sealed record GitHubRepositoryTarget
{
    public const string DescriptorKind = "github.repository-owner/v1";
    public string OwnerId { get; }
    public string RepositoryId { get; }

    public GitHubRepositoryTarget(string ownerId, string repositoryId)
    {
        OwnerId = ValidateId(ownerId);
        RepositoryId = ValidateId(repositoryId);
    }

    public ProviderTargetIdentity ToIdentity(Uri endpoint) => new(
        "github", endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant(), DescriptorKind,
        JsonSerializer.Serialize(new[] { OwnerId, RepositoryId }, TwigJsonContext.Default.StringArray));

    public static GitHubRepositoryTarget FromIdentity(ProviderTargetIdentity identity, Uri endpoint)
    {
        if (!string.Equals(identity.ProviderKind, "github", StringComparison.Ordinal)
            || !string.Equals(identity.ResourceScope, endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant(), StringComparison.Ordinal)
            || !string.Equals(identity.DescriptorKind, DescriptorKind, StringComparison.Ordinal))
            throw new InvalidDataException("provider-target-binding-mismatch: expected a GitHub repository target at this API origin.");
        var ids = JsonSerializer.Deserialize(identity.DescriptorValue, TwigJsonContext.Default.StringArray);
        if (ids is not { Length: 2 })
            throw new InvalidDataException("provider-target-binding-invalid: expected two opaque repository identity components.");
        var target = new GitHubRepositoryTarget(ids[0], ids[1]);
        if (target.ToIdentity(endpoint) != identity)
            throw new InvalidDataException("provider-target-binding-invalid: repository descriptor is not canonical.");
        return target;
    }

    private static string ValidateId(string id) =>
        !string.IsNullOrWhiteSpace(id) && string.Equals(id, id.Trim(), StringComparison.Ordinal)
            ? id : throw new InvalidDataException("GitHub returned an invalid opaque repository identity.");
}

internal sealed record GitHubRepositoryObservation(
    GitHubRepositoryTarget Target, string ResourceScope, string Locator);

/// <summary>Observes identity only; never creates a binding or changes an attachment.</summary>
internal sealed class GitHubRepositoryDiscovery(HttpClient http, GitHubWorkspaceScope scope)
{
    public async Task<GitHubRepositoryObservation> DiscoverAsync(CancellationToken ct = default)
    {
        if (GitHubWorkspaceScope.NormalizeRepository(scope.Repository) is null)
            throw new ArgumentException("A valid owner/name locator is required.");
        var parts = scope.Repository.Split('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, scope.GraphQlEndpoint);
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Content = new StringContent(JsonSerializer.Serialize(new GitHubIssueGraphQlRequest(
            GitHubIssueQueries.RepositoryDiscovery,
            new() { ["owner"] = parts[0], ["name"] = parts[1] }),
            TwigJsonContext.Default.GitHubIssueGraphQlRequest), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(body, TwigJsonContext.Default.GitHubIssueGraphQlResponse)
            ?? throw new JsonException("GitHub repository discovery returned no response.");
        if (result.Errors.ValueKind != JsonValueKind.Undefined
            && !(result.Errors.ValueKind == JsonValueKind.Array && result.Errors.GetArrayLength() == 0))
            throw new JsonException("GitHub repository discovery was incomplete or unavailable; absence is not established.");
        var repository = Required(result.Data, "repository");
        var target = new GitHubRepositoryTarget(Text(Required(repository, "owner"), "id"), Text(repository, "id"));
        var locator = Text(repository, "nameWithOwner");
        if (GitHubWorkspaceScope.NormalizeRepository(locator) is null)
            throw new JsonException("GitHub returned an invalid repository locator.");
        return new(target, scope.GraphQlEndpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant(), locator);
    }

    internal static JsonElement Required(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null
            ? value : throw new JsonException("GitHub repository identity is unavailable or incomplete; absence is not established.");

    internal static string Text(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new JsonException("GitHub returned an invalid repository identity field.");
    }
}
