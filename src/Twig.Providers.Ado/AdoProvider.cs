using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider : IProvider, IDisposable
{
    private const string Api = "7.1";
    private readonly Connection _connection;
    private readonly AdoAccess _access;
    private readonly IStore _store;
    private readonly HttpClient _http;
    private ProviderCapabilities _capabilities = new(
        [OpKind.Create, OpKind.Batch, OpKind.Relate, OpKind.Unrelate, OpKind.Comment], [], [], true, true);

    public AdoProvider(Connection connection, AdoAccess access, IStore store, HttpMessageHandler? transport = null)
    {
        if (connection.Provider.Value != "ado") throw new ArgumentException("ADO connection required.", nameof(connection));
        if (Wire.GuidText(connection.ResourceScope) != connection.ResourceScope
            || Wire.GuidText(connection.Destination) != connection.Destination)
            throw new ArgumentException("ADO scope and destination must be canonical collection/project GUIDs.", nameof(connection));
        (_connection, _access, _store) = (connection, access, store);
        _http = new HttpClient(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) };
    }

    public ProviderCapabilities Capabilities => _capabilities;
    public void Dispose() => _http.Dispose();

    private sealed record Bound(AdoSession Session, Connection Connection, Uri OrganizationUrl, string PrincipalId,
        AuthenticationHeaderValue Authorization, string ProjectName, string ProcessId)
    {
        internal Uri Url(string route) => new(OrganizationUrl.AbsoluteUri.TrimEnd('/') + "/" + route);
        internal string Project => Wire.Escape(Connection.Destination);
    }

    private async Task<Bound> Bind(Connection connection, AdoSession session, CancellationToken ct)
    {
        var acquiredConnection = session.Connection;
        var organization = session.OrganizationUrl;
        var principal = Wire.GuidText(session.PrincipalId);
        var authorization = session.Authorization;
        if (connection != _connection || acquiredConnection != connection
            || organization.Scheme != "https" || organization.UserInfo.Length != 0
            || organization.Query.Length != 0 || organization.Fragment.Length != 0)
            throw Wire.Refuse(ErrorCode.Conflict, "The acquired actor/locator does not match the immutable connection.");
        var bound = new Bound(session, connection, organization, principal, authorization, "", "");
        var identity = await Get(bound, "_apis/connectionData?api-version=7.1-preview", ct);
        if (Wire.GuidText(Wire.Text(identity, "instanceId")) != connection.ResourceScope
            || Wire.GuidText(Wire.Text(identity.GetProperty("authenticatedUser"), "id")) != principal)
            throw Wire.Refuse(ErrorCode.Conflict, "The native collection or authenticated principal moved.");
        var project = await Get(bound, $"_apis/projects/{bound.Project}?includeCapabilities=true&api-version={Api}", ct);
        if (Wire.GuidText(Wire.Text(project, "id")) != connection.Destination)
            throw Wire.Refuse(ErrorCode.Conflict, "Project lookup resolved a different immutable destination.");
        return bound with
        {
            ProjectName = Wire.Text(project, "name"),
            ProcessId = Wire.GuidText(Wire.Text(project.GetProperty("capabilities").GetProperty("processTemplate"), "templateTypeId")),
        };
    }

    private async Task<T> WithSession<T>(Connection connection, Func<Bound, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (connection != _connection) throw Wire.Refuse(ErrorCode.Conflict, "Connection differs from this adapter's binding.");
        var acquired = await _access.AcquireAsync(connection, ct);
        if (!acquired.IsSuccess) throw new AdoFailure(acquired.Error);
        using var session = acquired.Value;
        var bound = await Bind(connection, session, ct);
        return await action(bound, ct);
    }

    private static async Task<Result<T>> Guard<T>(Func<Task<T>> action)
    {
        try { return Result<T>.Ok(await action()); }
        catch (AdoFailure ex) { return Result<T>.Fail(ex.Error); }
        catch (HttpRequestException ex) { return Result<T>.Fail(ErrorCode.Unavailable, $"ADO transport unavailable before mutation dispatch: {ex.Message}"); }
        catch (Exception ex) when (Malformed(ex))
        { return Result<T>.Fail(ErrorCode.Unavailable, $"Invalid/incomplete ADO response: {ex.Message}"); }
    }

    private static bool Malformed(Exception ex) => ex is JsonException or KeyNotFoundException or InvalidOperationException
        or FormatException or OverflowException or ArgumentException;

    private async Task<JsonElement> Get(Bound bound, string route, CancellationToken ct)
        => await Request(bound, HttpMethod.Get, route, null, ct);

    private async Task<JsonElement> Request(Bound bound, HttpMethod method, string route, byte[]? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, bound.Url(route));
        request.Headers.Authorization = bound.Authorization;
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw HttpFailure(response.StatusCode);
        if (response.Headers.Contains("x-ms-continuationtoken"))
            throw Wire.Refuse(ErrorCode.Unsupported, "This metadata/read route returned unsupported pagination; no exhaustive result is claimed.");
        var bytes = await ResponseBytes(response, ct);
        var root = JsonSerializer.Deserialize(bytes, TwigJsonContext.Default.JsonElement);
        Wire.Unique(root);
        return root;
    }

    private static AdoFailure HttpFailure(HttpStatusCode status) => new(new(
        status switch
        {
            HttpStatusCode.NotFound => ErrorCode.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => ErrorCode.Conflict,
            _ => ErrorCode.Unavailable,
        }, $"ADO returned HTTP {(int)status}; no fallback schema, account, target or retry was used.")
        { Evidence = [$"http:{(int)status}"] });

    private static async Task<byte[]> ResponseBytes(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 8 * 1024 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) != 0)
        {
            if (buffer.Length + read > limit) throw Wire.Invalid("ADO response exceeds the bounded payload limit.");
            buffer.Write(bytes, 0, read);
        }
        return buffer.ToArray();
    }

    private ItemKey Key(int id) => ItemKey.Remote(_connection.Provider, _connection.ResourceScope, id.ToString(CultureInfo.InvariantCulture));
    private int Id(ItemKey key) => key.BelongsTo(_connection)
        && int.TryParse(key.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        && key.NativeId == id.ToString(CultureInfo.InvariantCulture)
        ? id : throw Wire.Refuse(ErrorCode.InvalidInput, "A canonical positive native identity in the exact collection is required.");
    private static Revision Revision(ItemKey key, int revision) => new(key, $"ado-rev:{revision.ToString(CultureInfo.InvariantCulture)}");
    private int RevisionNumber(Op op) => op.ExpectedRevision?.Item == op.Item
        && op.ExpectedRevision.Token.StartsWith("ado-rev:", StringComparison.Ordinal)
        && int.TryParse(op.ExpectedRevision.Token.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0 && Revision(op.Item, number) == op.ExpectedRevision
        ? number : throw Wire.Refuse(ErrorCode.Conflict, "Revision is not an ADO target-bound observation.");
}
