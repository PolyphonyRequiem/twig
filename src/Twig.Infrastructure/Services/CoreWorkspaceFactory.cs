using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Providers.Ado;
using Twig.Store.Sqlite;
using Twig.Infrastructure.GitHub;

namespace Twig.Infrastructure.Services;

internal sealed record CoreWorkspaceDocument(int Version, string DatabasePath, Guid DatabaseInstance,
    Connection Connection, string PrincipalId);
internal sealed record CoreTransportOverride(Func<HttpMessageHandler> CreateHandler);

internal sealed class CoreWorkspaceHandle(CoreWorkspace workspace, IDisposable provider,
    CoreWorkspaceDocument document, NativeSourceStamp source, AdoAccess? nativeAdoAccess = null) : IDisposable
{
    internal CoreWorkspace Workspace { get; } = workspace;
    internal CoreWorkspaceDocument Document { get; } = document;
    internal NativeSourceStamp Source { get; } = source;
    internal AdoAccess? NativeAdoAccess { get; } = nativeAdoAccess;
    public void Dispose() => provider.Dispose();
}

internal sealed class CoreWorkspaceFactory(TwigConfiguration configuration, TwigPaths paths,
    IConnectionBindingService bindings, IAuthenticationProvider authentication, CoreTransportOverride? transport = null,
    CoreGitHubWorkspaceHost? github = null)
{
    internal const string FileName = "core-workspace.json";
    internal string WorkspaceRoot => paths.RepoRoot;
    internal bool IsGitHub => configuration.ResolveWorkItemProviderKind() == "github";
    internal (int Up, int Down, int Sideways) TreeContextDepths
        => (configuration.Display.TreeDepthUp, configuration.Display.TreeDepthDown, configuration.Display.TreeDepthSideways);
    internal IReadOnlyList<(string Path, bool IncludeChildren)> QueryAreaDefaults
        => configuration.Defaults?.ResolveAreaPaths() ?? [];
    internal string ExecutionRegistryPath => bindings.RegistryPath;

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
            return document.Connection.Provider.Value == "github" && github is not null
                ? await github.OpenAsync(document, store, ct).ConfigureAwait(false)
                : Result<CoreWorkspaceHandle>.Fail(ErrorCode.Unavailable, "The selected Core native host is unavailable; no legacy writer was opened.");
        var origin = await bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        if (document.Connection.IdentityRef != CleanCoreAdoAccess.Identity(origin, document.PrincipalId)
            || origin.Identity.AdoPrincipalId is { } principal && principal != document.PrincipalId)
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "The selected native binding or actor differs from the initialized Core workspace.");
        var access = new CleanCoreAdoAccess(authentication, bindings, configuration, paths, origin,
            document.Connection, document.PrincipalId, store);
        var source = NativeSourceReader.Stamp(document, paths.RepoRoot, origin);
        var benches = UserBenchStore.ForRegistry(bindings.RegistryPath);
        var registered = await NativeCoreExecution.RegisterAsync(bindings.RegistryPath,
            new(document.DatabaseInstance, document.DatabasePath, paths.RepoRoot, document.Connection), ct).ConfigureAwait(false);
        if (!registered.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(registered.Error);
        var provider = new AdoProvider(document.Connection, access, store, transport?.CreateHandler());
        var workspace = new CoreWorkspace(document.Connection, provider, store,
            new ConfiguredPolicyProjectionSource(paths.RepoRoot), document.PrincipalId, paths.RepoRoot, document.DatabaseInstance,
            benches, configuration.Team, bindings.RegistryPath);
        return Result<CoreWorkspaceHandle>.Ok(new(workspace, provider, document, source, access));
    }

    internal async Task<Result<bool>> RevalidateSourceAsync(CoreWorkspaceHandle handle, CancellationToken ct)
    {
        var current = await OpenAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        using var captured = current.Value;
        if (captured.Source != handle.Source)
            return Result<bool>.Fail(ErrorCode.Conflict, "The native workspace, actor, store or authority changed; no content or rule change was released.");
        return await AdmitSourceAsync(captured, ct).ConfigureAwait(false);
    }

    internal async Task<Result<CoreWorkspaceHandle>> OpenScopedAsync(NativePlanScope scope, NativeSourceStamp expectedOrigin, CancellationToken ct)
    {
        var current = await OpenAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(current.Error);
        using var originHandle = current.Value;
        if (originHandle.Source != expectedOrigin || scope.Source != expectedOrigin
            || scope.State != NativePlanScopeState.Ready || !NativePathComparer.Instance.Equals(scope.OriginRoot, paths.RepoRoot))
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "The exact Plan origin, native actor or scope ownership changed.");
        var opened = await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(opened.Error);
        var pinned = await opened.Value.CheckWorkspaceAsync(scope.Source.Connection, ct).ConfigureAwait(false);
        if (!pinned.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(pinned.Error);
        var document = originHandle.Document with { DatabasePath = scope.DatabasePath, DatabaseInstance = scope.StoreInstance };
        if (document.Connection.Provider.Value == "github" && github is not null)
            return await github.OpenAsync(document, opened.Value, ct, scoped: true).ConfigureAwait(false);
        if (document.Connection.Provider.Value != "ado")
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Unsupported, "This Plan has no native execution composition.");
        var authority = await bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        var source = NativeSourceReader.Stamp(originHandle.Document, paths.RepoRoot, authority);
        if (source != expectedOrigin)
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "Native authoring authority changed while opening the Plan store.");
        var access = new CleanCoreAdoAccess(authentication, bindings, configuration, paths, authority,
            document.Connection, document.PrincipalId, opened.Value);
        var registered = await NativeCoreExecution.RegisterAsync(bindings.RegistryPath,
            new(scope.StoreInstance, scope.DatabasePath, paths.RepoRoot, document.Connection), ct).ConfigureAwait(false);
        if (!registered.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(registered.Error);
        var provider = new AdoProvider(document.Connection, access, opened.Value, transport?.CreateHandler());
        var workspace = new CoreWorkspace(document.Connection, provider, opened.Value,
            new ConfiguredPolicyProjectionSource(paths.RepoRoot), document.PrincipalId, paths.RepoRoot, scope.StoreInstance,
            executionRegistryPath: bindings.RegistryPath);
        return Result<CoreWorkspaceHandle>.Ok(new(workspace, provider, document,
            NativeSourceReader.Stamp(document, paths.RepoRoot, authority), access));
    }

    internal async Task<Result<bool>> AdmitSourceAsync(CoreWorkspaceHandle handle, CancellationToken ct)
    {
        if (handle.Source.Connection.Provider.Value != "ado") return Result<bool>.Ok(true);
        if (authentication is not IConnectionOperationGuard guard)
            return Result<bool>.Fail(ErrorCode.Unavailable, "Native operation admission is required.");
        using var lease = await guard.AcquireOperationAsync(ct).ConfigureAwait(false);
        if (lease is not IAdmittedConnectionOperation admitted || admitted.GetAuthority(bindings) is not { } origin
            || NativeSourceReader.Stamp(handle.Document, paths.RepoRoot, origin) != handle.Source)
            return Result<bool>.Fail(ErrorCode.Conflict, "The selected native authority changed.");
        return Result<bool>.Ok(true);
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
        Connection connection, string principal, CancellationToken ct, string? executionRegistryPath = null)
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
            if (executionRegistryPath is not null)
                return await NativeCoreExecution.RegisterAsync(executionRegistryPath,
                    new(store.InstanceId, store.DatabasePath, paths.RepoRoot, connection), ct).ConfigureAwait(false);
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
        services.AddCoreBootstrapServices(configuration, paths);
        services.AddSingleton<IAuthenticationProvider>(sp =>
            new DeferredBoundAuthenticationProvider(async ct =>
            {
                var resolver = sp.GetRequiredService<IConnectionBindingService>();
                var selected = await resolver.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
                return resolver.CreateAuthenticationProvider(selected);
            }));
        services.AddSingleton(sp => new CoreWorkspaceFactory(configuration, paths,
            sp.GetRequiredService<IConnectionBindingService>(), sp.GetRequiredService<IAuthenticationProvider>(),
            sp.GetService<CoreTransportOverride>(), configuration.ResolveWorkItemProviderKind() == "github"
                ? sp.GetService<CoreGitHubWorkspaceHost>() : null));
        services.AddSingleton(sp => new CoreGitHubWorkspaceHost(configuration, paths,
            sp.GetRequiredService<IConnectionBindingService>(), sp.GetService<IGitHubTokenSource>(),
            sp.GetService<CoreTransportOverride>(), sp.GetService<IBoundedProcessRunner>()));
        return services;
    }

    internal static IServiceCollection AddCoreBootstrapServices(this IServiceCollection services,
        TwigConfiguration configuration, TwigPaths paths)
    {
        services.AddSingleton(configuration);
        services.AddSingleton(paths);
        services.TryAddSingleton<IConnectionBindingService>(_ =>
            new ConnectionBindingService(ConnectionBindingService.ResolveUserHome()));
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
