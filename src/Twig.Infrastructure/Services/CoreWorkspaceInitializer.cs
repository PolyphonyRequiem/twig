using System.Net.Http.Headers;
using System.Text.Json;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal static class CoreWorkspaceInitializer
{
    internal static async Task<Result<NativeCreationReceipt>> InitializeWithEvidenceAsync(TwigConfiguration configuration, TwigPaths paths,
        IConnectionBindingService bindings, string databasePath, CancellationToken ct, CoreTransportOverride? transport = null,
        Twig.Infrastructure.GitHub.IBoundedProcessRunner? githubProcesses = null, NativeSetupEffects? effects = null,
        FreshWorkspaceInitialization? initialization = null)
    {
        if (!Path.IsPathFullyQualified(databasePath) || File.Exists(databasePath)
            || File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)))
            return Result<NativeCreationReceipt>.Fail(ErrorCode.InvalidInput, "Core initialization requires a genuinely new explicit absolute database file; nothing existing was adopted.");
        if (configuration.ResolveWorkItemProviderKind() == "twig")
            return await new CoreNativeWorkspaceHost(paths, bindings).InitializeAsync(databasePath, ct, effects).ConfigureAwait(false);
        if (configuration.ResolveWorkItemProviderKind() != "ado")
            return configuration.ResolveWorkItemProviderKind() == "github"
                ? await new Twig.Infrastructure.GitHub.CoreGitHubWorkspaceHost(configuration, paths, bindings, transport: transport, processes: githubProcesses)
                    .InitializeWithEvidenceAsync(databasePath, ct, effects).ConfigureAwait(false)
                : Result<NativeCreationReceipt>.Fail(ErrorCode.Unsupported, "The selected native Core initialization provider is unsupported.");
        effects?.Before(NativeSetupEffectKind.Registry);
        try
        {
            if (initialization is null || !NativePathComparer.Instance.Equals(Path.GetFullPath(databasePath), Path.GetFullPath(paths.DbPath)))
                return Result<NativeCreationReceipt>.Fail(ErrorCode.Unavailable, "Initialization requires the actual fresh checkout and exclusive binding operation leases.");
            using var authority = await bindings.CreateInitializationAuthorityAsync(configuration, paths, initialization, ct).ConfigureAwait(false);
            var origin = authority.Origin;
            if (origin.Identity.Method == "aad") effects?.Before(NativeSetupEffectKind.CredentialWrite);
            var token = await authority.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var authorization = token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? AuthenticationHeaderValue.Parse(token) : new AuthenticationHeaderValue("Bearer", token);
            var organization = new Uri(AdoEndpoint.NormalizeOrgUrl(origin.Operation.Organization).TrimEnd('/') + "/");
            if (organization.Scheme != "https" || organization.UserInfo.Length != 0
                || organization.Query.Length != 0 || organization.Fragment.Length != 0)
                return Result<NativeCreationReceipt>.Fail(ErrorCode.InvalidInput, "An explicit secure native organization locator is required.");
            using var http = new HttpClient(transport?.CreateHandler() ?? new SocketsHttpHandler { AllowAutoRedirect = false });
            var identity = await GetAsync("_apis/connectionData?api-version=7.1-preview");
            var collection = GuidText(identity.GetProperty("instanceId"));
            var principal = GuidText(identity.GetProperty("authenticatedUser").GetProperty("id"));
            if (origin.Identity.AdoPrincipalId is { } expected && expected != principal)
                return Result<NativeCreationReceipt>.Fail(ErrorCode.Conflict, "Native initialization observed a different selected principal.");
            var project = await GetAsync("_apis/projects/" + Uri.EscapeDataString(origin.Operation.Project) + "?includeCapabilities=true&api-version=7.1");
            var destination = GuidText(project.GetProperty("id"));
            await authority.RevalidateAsync(ct).ConfigureAwait(false);
            var connection = new Connection(new("ado"), collection, destination, CleanCoreAdoAccess.Identity(origin, principal));
            var parent = Path.GetDirectoryName(databasePath)!;
            if (!Directory.Exists(parent))
                return Result<NativeCreationReceipt>.Fail(ErrorCode.InvalidInput, "The explicitly selected database directory must already exist.");
            effects?.Before(NativeSetupEffectKind.CoreStore);
            var created = await SqliteStore.CreateAsync(databasePath, ct).ConfigureAwait(false);
            if (!created.IsSuccess) return Result<NativeCreationReceipt>.Fail(created.Error);
            effects?.Confirm(NativeSetupEffectKind.CoreStore);
            initialization.RecordCreatedStore(created.Value);
            await authority.RevalidateAsync(ct).ConfigureAwait(false);
            CoreWorkspaceDocument? recorded = null;
            var recording = await CoreWorkspaceFactory.RecordNewAsync(paths, created.Value, databasePath, connection, principal, ct,
                bindings.RegistryPath, effects, document => recorded = document).ConfigureAwait(false);
            if (!recording.IsSuccess) return Result<NativeCreationReceipt>.Fail(recording.Error);
            if (recorded is null)
                return Result<NativeCreationReceipt>.Fail(ErrorCode.Unavailable, "Native initialization did not retain its created document evidence.");
            var currentConfiguration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
            var current = await bindings.ResolveAsync(currentConfiguration, paths, ct).ConfigureAwait(false);
            if (current with { StorageGeneration = null } != origin)
                return Result<NativeCreationReceipt>.Fail(ErrorCode.Conflict, "The native binding changed while the new store was recorded.");
            return Result<NativeCreationReceipt>.Ok(new(recorded, NativeSourceReader.Stamp(recorded, paths.RepoRoot, current)));

            async Task<JsonElement> GetAsync(string route)
            {
                await authority.RevalidateAsync(ct).ConfigureAwait(false);
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
        catch (NativeSetupException ex)
        {
            return Result<NativeCreationReceipt>.Fail(ex.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException)
        {
            return Result<NativeCreationReceipt>.Fail(ErrorCode.Unavailable,
                "Core initialization could not establish the selected native identity; created files are preserved for inspection. " + ex.Message);
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
