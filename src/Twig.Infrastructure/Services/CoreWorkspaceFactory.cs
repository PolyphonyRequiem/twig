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

internal sealed class CoreWorkspaceHandle(CoreWorkspace workspace, IDisposable provider) : IDisposable
{
    internal CoreWorkspace Workspace { get; } = workspace;
    public void Dispose() => provider.Dispose();
}

internal sealed class CoreWorkspaceFactory(TwigConfiguration configuration, TwigPaths paths,
    IConnectionBindingService bindings, IAuthenticationProvider authentication, CoreTransportOverride? transport = null)
{
    internal const string FileName = "core-workspace.json";
    internal string WorkspaceRoot => paths.RepoRoot;

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
            new ConfiguredPolicyProjectionSource(paths.RepoRoot), document.PrincipalId, paths.RepoRoot);
        return Result<CoreWorkspaceHandle>.Ok(new(workspace, provider));
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
