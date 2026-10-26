using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.GitHub;

/// <summary>Raised when a request is refused by the read-only admission policy. Never carries the token.</summary>
internal sealed class GitHubReadOnlyRequestRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Epic #1167 read-only enforcement for the GitHub adapter. A request is admitted only when it
/// is a POST to the exact configured GraphQL endpoint whose body is exactly one of the adapter's
/// own fixed read documents (<see cref="GitHubIssueQueries"/>) with the expected variable shape.
/// Everything else is refused before the token is requested or the network is touched. Redirects
/// are refused, so the bearer token never follows a response to another destination.
/// Twig enforces read-only here; the token's real scope is whatever the user granted <c>gh</c>.
/// </summary>
internal sealed class GitHubReadOnlyAuthHandler(IGitHubTokenSource tokenSource, Uri endpoint) : DelegatingHandler
{
    private const int MaxBodyBytes = 64 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await AdmitAsync(request, ct).ConfigureAwait(false);

        var token = await tokenSource.GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            response.Dispose();
            throw new GitHubReadOnlyRequestRefusedException(
                $"GitHub returned a redirect ({status}); redirects are refused for the read-only adapter.");
        }
        return response;
    }

    private async Task AdmitAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post)
            throw Refuse("only the adapter's read-query POST is permitted");
        if (request.RequestUri is null || !IsExactEndpoint(request.RequestUri))
            throw Refuse("the request is not addressed to the configured GitHub GraphQL endpoint");
        if (request.Headers.Authorization is not null)
            throw Refuse("the request already carries credentials");
        if (request.Content is null)
            throw Refuse("the request has no query body");

        var body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (body.Length > MaxBodyBytes)
            throw Refuse("the request body is too large");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw Refuse("the request body is not a recognized read query");
        }

        using (document)
        {
            if (!IsAdmittedEnvelope(document.RootElement))
                throw Refuse("the request body is not a recognized read query");
        }
    }

    private bool IsExactEndpoint(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && string.Equals(uri.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == endpoint.Port
        && string.Equals(uri.AbsolutePath, endpoint.AbsolutePath, StringComparison.Ordinal);

    private static bool IsAdmittedEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement? query = null, variables = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "query" when query is null:
                    query = property.Value;
                    break;
                case "variables" when variables is null:
                    variables = property.Value;
                    break;
                default:
                    return false;
            }
        }

        if (query is not { ValueKind: JsonValueKind.String } q || variables is not { ValueKind: JsonValueKind.Object } v)
            return false;
        var discovery = string.Equals(q.GetString(), GitHubIssueQueries.RepositoryDiscovery, StringComparison.Ordinal);
        if (discovery)
            return IsDiscoveryVariables(v);
        if (!GitHubIssueQueries.TryMatch(q.GetString()!, out var paginated))
            return false;

        bool sawId = false, sawAfter = false;
        foreach (var variable in v.EnumerateObject())
        {
            switch (variable.Name)
            {
                case "id" when !sawId:
                    if (variable.Value.ValueKind != JsonValueKind.String || variable.Value.GetString()!.Length == 0)
                        return false;
                    sawId = true;
                    break;
                case "after" when paginated && !sawAfter:
                    if (variable.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        return false;
                    sawAfter = true;
                    break;
                default:
                    return false;
            }
        }
        return sawId && sawAfter == paginated;
    }

    private static bool IsDiscoveryVariables(JsonElement variables)
    {
        string? owner = null, name = null;
        foreach (var variable in variables.EnumerateObject())
        {
            if (variable.Value.ValueKind != JsonValueKind.String)
                return false;
            switch (variable.Name)
            {
                case "owner" when owner is null:
                    owner = variable.Value.GetString();
                    break;
                case "name" when name is null:
                    name = variable.Value.GetString();
                    break;
                default:
                    return false;
            }
        }
        return owner is not null && name is not null
            && GitHubWorkspaceScope.NormalizeRepository(owner + "/" + name) is not null;
    }

    private static GitHubReadOnlyRequestRefusedException Refuse(string reason) =>
        new($"GitHub request refused: {reason}.");

    /// <summary>
    /// Builds the dedicated GitHub client: this handler over a sockets handler with automatic
    /// redirects disabled. It is never the shared Azure DevOps client.
    /// </summary>
    internal static HttpClient CreateClient(IGitHubTokenSource tokenSource, Uri endpoint, HttpMessageHandler? inner = null)
    {
        var handler = new GitHubReadOnlyAuthHandler(tokenSource, endpoint)
        {
            InnerHandler = inner ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
    }
}
