using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Twig.Core;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Providers.Ado;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal sealed record CoreWorkspaceDocument(int Version, string DatabasePath, Guid DatabaseInstance,
    Connection Connection, string PrincipalId);
internal sealed record CoreTransportOverride(Func<HttpMessageHandler> CreateHandler);

internal sealed class CoreWorkspaceHandle(CoreWorkspace workspace, IDisposable provider, AdoAccess? nativeAdoAccess = null) : IDisposable
{
    internal CoreWorkspace Workspace { get; } = workspace;
    internal AdoAccess? NativeAdoAccess { get; } = nativeAdoAccess;
    public void Dispose() => provider.Dispose();
}

internal sealed class CoreWorkspaceFactory(TwigConfiguration configuration, TwigPaths paths,
    IConnectionBindingService bindings, IAuthenticationProvider authentication, CoreTransportOverride? transport = null)
{
    internal const string FileName = "core-workspace.json";
    internal string WorkspaceRoot => paths.RepoRoot;
    internal (int Up, int Down, int Sideways) TreeContextDepths
        => (configuration.Display.TreeDepthUp, configuration.Display.TreeDepthDown, configuration.Display.TreeDepthSideways);
    internal IReadOnlyList<(string Path, bool IncludeChildren)> QueryAreaDefaults
        => configuration.Defaults?.ResolveAreaPaths() ?? [];

    internal async Task<Result<CoreWorkspaceHandle>> OpenAsync(CancellationToken ct)
    {
        CoreWorkspaceDocument? document;
        try
        {
            await using var file = File.OpenRead(Path.Combine(paths.TwigDir, FileName));
            document = await JsonSerializer.DeserializeAsync(file, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.StorageUnavailable,
                "The explicitly initialized Core workspace is unavailable or incompatible; its existing work was not reset.");
        }
        if (document is not { Version: 1 } || string.IsNullOrWhiteSpace(document.DatabasePath)
            || !Path.IsPathFullyQualified(document.DatabasePath) || document.Connection is null
            || string.IsNullOrWhiteSpace(document.PrincipalId)
            || document.DatabaseInstance == Guid.Empty || document.Connection.Provider.Value != configuration.ResolveWorkItemProviderKind())
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.InvalidInput, "The explicit Core workspace binding is invalid.");
        var opened = await SqliteStore.OpenAsync(document.DatabasePath, ct, document.DatabaseInstance).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(opened.Error);
        var store = opened.Value;
        var admitted = await store.CheckWorkspaceAsync(document.Connection, ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(admitted.Error);
        if (document.Connection.Provider.Value != "ado")
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Unavailable, "The GitHub Core host admission bridge is not yet wired in this in-progress cutover.");
        var origin = await bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        if (document.Connection.IdentityRef != CleanCoreAdoAccess.Identity(origin, document.PrincipalId)
            || origin.Identity.AdoPrincipalId is { } principal && principal != document.PrincipalId)
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "The selected native binding or actor differs from the initialized Core workspace.");
        var access = new CleanCoreAdoAccess(authentication, bindings, configuration, paths, origin,
            document.Connection, document.PrincipalId, store);
        var provider = new AdoProvider(document.Connection, access, store, transport?.CreateHandler());
        var workspace = new CoreWorkspace(document.Connection, provider, store,
            new ConfiguredPolicyProjectionSource(paths.RepoRoot), document.PrincipalId, paths.RepoRoot, document.DatabaseInstance);
        return Result<CoreWorkspaceHandle>.Ok(new(workspace, provider, access));
    }

    internal async Task<Result<ResourceLink>> ResolveBranchAsync(string branch, string? repository, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsControl))
            return Result<ResourceLink>.Fail(ErrorCode.InvalidInput, "An explicit branch name is required.");
        var opened = await OpenAsync(ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<ResourceLink>.Fail(opened.Error);
        using var handle = opened.Value;
        if (handle.NativeAdoAccess is null)
            return Result<ResourceLink>.Fail(ErrorCode.Unsupported, "Native branch-artifact resolution is unavailable for this provider.");
        var acquired = await handle.NativeAdoAccess.AcquireAsync(handle.Workspace.Connection, ct).ConfigureAwait(false);
        if (!acquired.IsSuccess) return Result<ResourceLink>.Fail(acquired.Error);
        using var session = acquired.Value;
        using var http = new HttpClient(transport?.CreateHandler() ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        try
        {
            var organization = new Uri(session.OrganizationUrl.AbsoluteUri.TrimEnd('/') + "/");
            var projectLocator = configuration.GetGitProject() ?? configuration.Project;
            var project = await Read("_apis/projects/" + Uri.EscapeDataString(projectLocator) + "?api-version=7.1");
            var projectId = NativeGuid(project.GetProperty("id"));
            var repositoryLocator = repository ?? configuration.Git.Repository;
            if (string.IsNullOrWhiteSpace(repositoryLocator))
                return Result<ResourceLink>.Fail(ErrorCode.InvalidInput, "Branch linking requires --repo or an explicitly configured repository; no default repository was guessed.");
            var nativeRepository = await Read(projectId + "/_apis/git/repositories/"
                + Uri.EscapeDataString(repositoryLocator) + "?api-version=7.1");
            var repositoryId = NativeGuid(nativeRepository.GetProperty("id"));
            if (NativeGuid(nativeRepository.GetProperty("project").GetProperty("id")) != projectId)
                return Result<ResourceLink>.Fail(ErrorCode.Conflict, "The observed Git repository belongs to another immutable project.");
            var refs = await Read(projectId + "/_apis/git/repositories/" + repositoryId + "/refs?filter="
                + Uri.EscapeDataString("heads/" + branch) + "&$top=2&api-version=7.1");
            var observed = refs.GetProperty("value").EnumerateArray()
                .Where(value => value.GetProperty("name").GetString() == "refs/heads/" + branch).ToArray();
            if (observed.Length != 1)
                return Result<ResourceLink>.Fail(ErrorCode.Unavailable, "The exact branch was not uniquely observed; no artifact or creation was staged.");
            return Result<ResourceLink>.Ok(new("ArtifactLink",
                $"vstfs:///Git/Ref/{projectId}/{repositoryId}/GB{Uri.EscapeDataString(branch)}", "Branch"));

            async Task<JsonElement> Read(string route)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(organization, route));
                request.Headers.Authorization = session.Authorization;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Headers.Contains("x-ms-continuationtoken"))
                    throw new HttpRequestException("Native branch metadata was unavailable or paginated; no redirect/retry was used.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + read > 2 * 1024 * 1024) throw new JsonException("Branch metadata exceeded the input bound.");
                    buffer.Write(chunk, 0, read);
                }
                using var document = JsonDocument.Parse(buffer.ToArray());
                Unique(document.RootElement);
                return document.RootElement.Clone();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return Result<ResourceLink>.Fail(ErrorCode.Unavailable, "Native branch metadata could not bind the reviewed artifact: " + ex.Message);
        }

        static string NativeGuid(JsonElement value) => Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty
            ? id.ToString("D") : throw new JsonException("Invalid immutable native Git identity.");
        static void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate native Git metadata member.");
                    Unique(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }

    internal static async Task<Result<bool>> RecordNewAsync(TwigPaths paths, SqliteStore store, string databasePath,
        Connection connection, string principal, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(databasePath) || store.InstanceId == Guid.Empty)
            return Result<bool>.Fail(ErrorCode.InvalidInput, "A new explicitly located native store instance is required.");
        var pinned = await store.PinWorkspaceAsync(connection, ct).ConfigureAwait(false);
        if (!pinned.IsSuccess) return pinned;
        try
        {
            await using var file = new FileStream(Path.Combine(paths.TwigDir, FileName), FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(file, new CoreWorkspaceDocument(1, databasePath, store.InstanceId,
                connection, principal), CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument, ct).ConfigureAwait(false);
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            return Result<bool>.Ok(true);
        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<bool>.Fail(ErrorCode.StorageUnavailable,
                "Core initialization did not complete; the selected store and any existing marker were preserved. " + ex.Message);
        }
    }
}

internal static class CoreWorkspaceRegistration
{
    internal static IServiceCollection AddCoreWorkspaceServices(this IServiceCollection services,
        TwigConfiguration configuration, TwigPaths paths)
    {
        services.AddSingleton(configuration);
        services.AddSingleton(paths);
        services.TryAddSingleton<IConnectionBindingService>(_ =>
            new ConnectionBindingService(ConnectionBindingService.ResolveUserHome()));
        services.AddSingleton<IAuthenticationProvider>(sp =>
            new DeferredBoundAuthenticationProvider(async ct =>
            {
                var resolver = sp.GetRequiredService<IConnectionBindingService>();
                var selected = await resolver.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
                return resolver.CreateAuthenticationProvider(selected);
            }));
        services.AddSingleton(sp => new CoreWorkspaceFactory(configuration, paths,
            sp.GetRequiredService<IConnectionBindingService>(), sp.GetRequiredService<IAuthenticationProvider>(),
            sp.GetService<CoreTransportOverride>()));
        return services;
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CoreWorkspaceDocument))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CoreWorkspaceJsonContext : JsonSerializerContext;
