using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider : IProvider, IDisposable
{
    private readonly Connection _connection;
    private readonly GitHubAccess _access;
    private readonly IStore _store;
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repository;
    private ProviderCapabilities _capabilities = new([], [], [], false, false);

    public GitHubProvider(Connection connection, GitHubAccess access, IStore store, HttpMessageHandler? transport = null)
    {
        if (connection.Provider.Value != "github"
            || !Uri.TryCreate(connection.ResourceScope, UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.GetLeftPart(UriPartial.Authority).ToLowerInvariant() != connection.ResourceScope)
            throw new ArgumentException("GitHub requires a canonical HTTPS API origin.", nameof(connection));
        var ids = Wire.Parse(System.Text.Encoding.UTF8.GetBytes(connection.Destination));
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 2)
            throw new ArgumentException("Destination must encode immutable [ownerId, repositoryId].", nameof(connection));
        _owner = Wire.Id(ids[0].GetString()!);
        _repository = Wire.Id(ids[1].GetString()!);
        if (Destination(_owner, _repository) != connection.Destination)
            throw new ArgumentException("Destination identity is not canonical.", nameof(connection));
        (_connection, _access, _store) = (connection, access, store);
        _http = new(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static string Destination(string ownerId, string repositoryId) => Wire.Utf8(Wire.Json(json =>
    {
        json.WriteStartArray(); json.WriteStringValue(Wire.Id(ownerId)); json.WriteStringValue(Wire.Id(repositoryId)); json.WriteEndArray();
    }));

    public ProviderCapabilities Capabilities => _capabilities;
    public void Dispose() => _http.Dispose();

    private sealed record Bound(GitHubSession Session, Uri Endpoint, string Actor, string Authority,
        AuthenticationHeaderValue Authorization);

    private async Task<T> WithSession<T>(Connection connection, Func<Bound, CancellationToken, Task<T>> action, CancellationToken ct,
        bool validateAfter = true)
    {
        if (connection != _connection) throw Wire.Refuse(ErrorCode.Conflict, "Connection differs from the immutable adapter binding.");
        var acquired = await _access.AcquireAsync(connection, ct);
        if (!acquired.IsSuccess) throw new GitHubFailure(acquired.Error);
        using var session = acquired.Value;
        var endpoint = session.Endpoint;
        if (session.Connection != connection || endpoint.Scheme != "https"
            || endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant() != connection.ResourceScope
            || endpoint.AbsolutePath is not ("/graphql" or "/api/graphql")
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !Enum.IsDefined(session.CredentialSource) || string.IsNullOrWhiteSpace(session.AcquisitionContext)
            || session.Authorization.Scheme != "Bearer" || string.IsNullOrWhiteSpace(session.Authorization.Parameter))
            throw Wire.Refuse(ErrorCode.Conflict, "Selected credential acquisition/endpoint context is invalid; no alternative account is used.");
        var actor = Wire.Id(session.ActorId);
        var authority = Wire.Hash(Wire.Json(json =>
        {
            json.WriteStartArray(); json.WriteStringValue(actor); json.WriteStringValue(endpoint.AbsoluteUri);
            json.WriteStringValue(session.AcquisitionContext); json.WriteNumberValue((int)session.CredentialSource); json.WriteEndArray();
        }));
        var bound = new Bound(session, endpoint, actor, authority, session.Authorization);
        var data = await Graph(bound, "query($id: ID!) { viewer { id } node(id: $id) { __typename id ... on Repository { owner { id } } } }",
            json => json.WriteString("id", _repository), ct);
        if (Wire.Text(Wire.Member(data, "viewer"), "id") != actor)
            throw Wire.Refuse(ErrorCode.Conflict, "Authenticated native actor changed.");
        Repository(Wire.Member(data, "node"));
        await session.ValidateAsync(ct);
        var result = await action(bound, ct);
        if (validateAfter) await session.ValidateAsync(ct);
        return result;
    }

    private void Repository(JsonElement repository)
    {
        if (Wire.Text(repository, "id") != _repository || Wire.Text(Wire.Member(repository, "owner"), "id") != _owner)
            throw Wire.Refuse(ErrorCode.Conflict, "Immutable repository or owner changed; explicit rebind is required.");
        if (repository.TryGetProperty("__typename", out var kind) && kind.GetString() != "Repository")
            throw Wire.Invalid("Native repository identity has a different kind.");
    }

    private ItemKey Key(string id) => ItemKey.Remote(_connection.Provider, _connection.ResourceScope, Wire.Id(id));
    private string Id(ItemKey key) => key.BelongsTo(_connection)
        ? Wire.Id(key.NativeId!) : throw Wire.Refuse(ErrorCode.InvalidInput, "Exact GitHub resource scope and native node ID required.");

    private static bool Malformed(Exception ex) => ex is JsonException or KeyNotFoundException or InvalidOperationException
        or FormatException or OverflowException or ArgumentException;

    private static async Task<Result<T>> Guard<T>(Func<Task<T>> action)
    {
        try { return Result<T>.Ok(await action()); }
        catch (GitHubFailure ex) { return Result<T>.Fail(ex.Error); }
        catch (Exception ex) when (ex is HttpRequestException or IOException || Malformed(ex))
        { return Result<T>.Fail(ErrorCode.Unavailable, $"GitHub observation unavailable ({ex.GetType().Name}); no mutation fallback or retry."); }
    }

    private HttpRequestMessage Request(Bound bound, byte[] payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, bound.Endpoint);
        request.Headers.Authorization = bound.Authorization;
        request.Headers.UserAgent.ParseAdd("twig-cli");
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new("application/json");
        return request;
    }

    private async Task<JsonElement> Graph(Bound bound, string document, Action<Utf8JsonWriter>? variables, CancellationToken ct)
    {
        using var request = Request(bound, Wire.Request(document, variables));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw Wire.Refuse(ErrorCode.Unavailable, $"GitHub observation returned HTTP {(int)response.StatusCode}; no fallback or retry.");
        return Wire.Data(Wire.Parse(await Wire.Response(response, ct)));
    }
}
