using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>A point-in-time observation, not authorization or a binding for future writes. Login is a locator only.</summary>
internal sealed record GitHubAuthenticatedActorObservation(
    string ActorId, string ResourceScope, string Login, string Source);

/// <summary>Explicitly observes the authenticated viewer through the existing read-only credential transport.</summary>
internal sealed class GitHubAuthenticatedActorReader(HttpClient http, GitHubWorkspaceScope scope) : IDisposable
{
    internal const string EvidenceSource = "github.graphql.viewer";

    public async Task<GitHubAuthenticatedActorObservation> ObserveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, scope.GraphQlEndpoint);
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Content = new StringContent(JsonSerializer.Serialize(
            new GitHubIssueGraphQlRequest(GitHubIssueQueries.AuthenticatedViewer, new()),
            TwigJsonContext.Default.GitHubIssueGraphQlRequest), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(body))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("GitHub authenticated actor observation returned an invalid envelope.");
            bool sawData = false, sawErrors = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "data")
                {
                    if (sawData)
                        throw new JsonException("GitHub authenticated actor observation contained ambiguous data.");
                    sawData = true;
                }
                else if (property.Name == "errors")
                {
                    if (sawErrors)
                        throw new JsonException("GitHub authenticated actor observation contained ambiguous errors.");
                    sawErrors = true;
                }
            }
        }
        var result = JsonSerializer.Deserialize(body, TwigJsonContext.Default.GitHubIssueGraphQlResponse)
            ?? throw new JsonException("GitHub authenticated actor observation returned no response.");
        if (result.Errors.ValueKind != JsonValueKind.Undefined
            && !(result.Errors.ValueKind == JsonValueKind.Array && result.Errors.GetArrayLength() == 0))
            throw new JsonException("GitHub authenticated actor observation was incomplete or unavailable.");
        var viewer = Required(result.Data, "viewer");
        var actorId = CanonicalText(Required(viewer, "id"));
        var login = CanonicalText(Required(viewer, "login"));
        return new(actorId, scope.GraphQlEndpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant(),
            login, EvidenceSource);
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new JsonException("GitHub authenticated actor observation was incomplete or unavailable.");
        JsonElement? result = null;
        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name != name)
                continue;
            if (result is not null)
                throw new JsonException("GitHub authenticated actor observation contained ambiguous identity fields.");
            result = property.Value;
        }
        return result ?? throw new JsonException("GitHub authenticated actor observation was incomplete or unavailable.");
    }

    private static string CanonicalText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException("GitHub authenticated actor observation contained an invalid identity field.");
        var text = value.GetString()!;
        return !string.IsNullOrWhiteSpace(text) && string.Equals(text, text.Trim(), StringComparison.Ordinal)
            && !text.Any(char.IsControl)
            ? text : throw new JsonException("GitHub authenticated actor observation contained an invalid identity field.");
    }

    public void Dispose() => http.Dispose();
}
