using System.Net.Http.Headers;
using System.Text.Json;
using Twig.Core;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal static class CoreWorkspaceInitializer
{
    internal static async Task<Result<bool>> InitializeAsync(TwigConfiguration configuration, TwigPaths paths,
        IConnectionBindingService bindings, string databasePath, CancellationToken ct, CoreTransportOverride? transport = null)
    {
        if (!Path.IsPathFullyQualified(databasePath) || File.Exists(databasePath)
            || File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)))
            return Result<bool>.Fail(ErrorCode.InvalidInput, "Core initialization requires a genuinely new explicit absolute database file; nothing existing was adopted.");
        if (configuration.ResolveWorkItemProviderKind() != "ado")
            return Result<bool>.Fail(ErrorCode.Unavailable, "GitHub Core initialization requires the retained native target/admission integration; no ADO target was substituted.");
        var origin = await bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        var authentication = bindings.CreateAuthenticationProvider(origin);
        try
        {
            if (authentication is not IConnectionOperationGuard guard)
                return Result<bool>.Fail(ErrorCode.Unavailable, "Initialization requires the actual native binding operation lease.");
            using var lease = await guard.AcquireOperationAsync(ct).ConfigureAwait(false);
            if (lease is not IAdmittedConnectionOperation admitted || admitted.GetAuthority(bindings) is not { } bound
                || !ConnectionRemoteWriteAdmission.SameAuthority(bound, origin))
                return Result<bool>.Fail(ErrorCode.Conflict, "The selected native binding changed during Core initialization.");
            var token = await authentication.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var authorization = token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? AuthenticationHeaderValue.Parse(token) : new AuthenticationHeaderValue("Bearer", token);
            var organization = new Uri(AdoRestClient.NormalizeOrgUrl(origin.Operation.Organization).TrimEnd('/') + "/");
            if (organization.Scheme != "https" || organization.UserInfo.Length != 0
                || organization.Query.Length != 0 || organization.Fragment.Length != 0)
                return Result<bool>.Fail(ErrorCode.InvalidInput, "An explicit secure native organization locator is required.");
            using var http = new HttpClient(transport?.CreateHandler() ?? new SocketsHttpHandler { AllowAutoRedirect = false });
            var identity = await GetAsync("_apis/connectionData?api-version=7.1-preview");
            var collection = GuidText(identity.GetProperty("instanceId"));
            var principal = GuidText(identity.GetProperty("authenticatedUser").GetProperty("id"));
            if (origin.Identity.AdoPrincipalId is { } expected && expected != principal)
                return Result<bool>.Fail(ErrorCode.Conflict, "Native initialization observed a different selected principal.");
            var project = await GetAsync("_apis/projects/" + Uri.EscapeDataString(origin.Operation.Project) + "?includeCapabilities=true&api-version=7.1");
            var destination = GuidText(project.GetProperty("id"));
            var current = await bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
            if (!ConnectionRemoteWriteAdmission.SameAuthority(current, origin))
                return Result<bool>.Fail(ErrorCode.Conflict, "The native binding changed before the new store was recorded.");
            var connection = new Connection(new("ado"), collection, destination, CleanCoreAdoAccess.Identity(origin, principal));
            var parent = Path.GetDirectoryName(databasePath)!;
            if (!Directory.Exists(parent))
                return Result<bool>.Fail(ErrorCode.InvalidInput, "The explicitly selected database directory must already exist.");
            var created = await SqliteStore.CreateAsync(databasePath, ct).ConfigureAwait(false);
            if (!created.IsSuccess) return Result<bool>.Fail(created.Error);
            return await CoreWorkspaceFactory.RecordNewAsync(paths, created.Value, databasePath, connection, principal, ct).ConfigureAwait(false);

            async Task<JsonElement> GetAsync(string route)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(organization, route));
                request.Headers.Authorization = authorization;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Native initialization metadata returned HTTP {(int)response.StatusCode}; no redirect or retry was used.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + read > 8 * 1024 * 1024) throw new JsonException("Native initialization metadata exceeded the input bound.");
                    buffer.Write(chunk, 0, read);
                }
                var json = JsonSerializer.Deserialize(buffer.ToArray(), CoreWorkspaceJsonContext.Default.JsonElement);
                Unique(json);
                return json;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException)
        {
            return Result<bool>.Fail(ErrorCode.Unavailable,
                "Core initialization could not establish the selected native identity; created files are preserved for inspection. " + ex.Message);
        }
        finally
        {
            if (authentication is IDisposable disposable) disposable.Dispose();
        }
    }

    private static string GuidText(JsonElement value)
    {
        if (!Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty) throw new JsonException("Invalid native immutable identity.");
        return id.ToString("D");
    }

    private static void Unique(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate native metadata property.");
                Unique(property.Value);
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
            foreach (var child in json.EnumerateArray()) Unique(child);
    }
}
