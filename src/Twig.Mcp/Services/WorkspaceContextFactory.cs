using System.Collections.Concurrent;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Mcp.Services;

/// <summary>
/// Creates and caches <see cref="WorkspaceContext"/> instances per <see cref="WorkspaceKey"/>.
/// Extracted for testability of <see cref="WorkspaceResolver"/>.
/// </summary>
public interface IWorkspaceContextFactory
{
    /// <summary>
    /// Gets or creates a <see cref="WorkspaceContext"/> for the given workspace key.
    /// </summary>
    WorkspaceContext GetOrCreate(WorkspaceKey key);
}

/// <summary>
/// Creates and caches <see cref="WorkspaceContext"/> instances per <see cref="WorkspaceKey"/>.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/> with <see cref="Lazy{T}"/>.
/// Shares global singletons (<see cref="HttpClient"/>, <see cref="IAuthenticationProvider"/>)
/// across workspaces; constructs per-workspace instances of all repos, stores, ADO clients,
/// and domain services — mirroring the wiring in <c>TwigServiceRegistration</c> +
/// <c>NetworkServiceModule</c> + <c>Program.cs</c> but instantiating directly rather than via DI.
/// </summary>
public sealed class WorkspaceContextFactory : IWorkspaceContextFactory, IDisposable
{
    private readonly WorkspaceRegistry _registry;
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _twigRoot;
    private readonly ConcurrentDictionary<WorkspaceKey, Lazy<WorkspaceContext>> _contexts = new();
    private bool _disposed;

    /// <param name="registry">Discovers available workspaces.</param>
    /// <param name="httpClient">Shared HTTP client for all ADO calls.</param>
    /// <param name="authProvider">Shared auth provider for all ADO calls.</param>
    /// <param name="twigRoot">Path to the <c>.twig/</c> directory (used to locate per-workspace DBs).</param>
    public WorkspaceContextFactory(
        WorkspaceRegistry registry,
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string twigRoot)
    {
        _registry = registry;
        _httpClient = httpClient;
        _authProvider = authProvider;
        _twigRoot = twigRoot;
    }

    /// <summary>
    /// Gets or creates a <see cref="WorkspaceContext"/> for the given workspace key.
    /// The context is lazily created on first access and cached for the process lifetime.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="key"/> is not a registered workspace.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the factory has been disposed.
    /// </exception>
    public WorkspaceContext GetOrCreate(WorkspaceKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _contexts.GetOrAdd(
            key,
            k => new Lazy<WorkspaceContext>(() => CreateContext(k))).Value;
    }

    private WorkspaceContext CreateContext(WorkspaceKey key)
    {
        var config = _registry.GetConfig(key);
        var paths = TwigPaths.BuildPaths(_twigRoot, config);

        // Persistence layer — one SqliteCacheStore per workspace
        var cacheStore = new SqliteCacheStore($"Data Source={paths.DbPath}");

        var workItemRepo = new SqliteWorkItemRepository(cacheStore);
        var contextStore = new SqliteContextStore(cacheStore);
        var pendingChangeStore = new SqlitePendingChangeStore(cacheStore);
        var linkRepo = new SqliteWorkItemLinkRepository(cacheStore);
        var fieldDefStore = new SqliteFieldDefinitionStore(cacheStore);
        var processTypeStore = new SqliteProcessTypeStore(cacheStore);

        // Process configuration
        var processConfigProvider = new DynamicProcessConfigProvider(processTypeStore);

        // Network layer — ADO clients (shares global HttpClient + AuthProvider)
        var adoService = new AdoRestClient(
            _httpClient,
            _authProvider,
            config.Organization,
            config.Project,
            fieldDefStore);

        var team = string.IsNullOrWhiteSpace(config.Team)
            ? $"{config.Project} Team"
            : config.Team;
        var iterationService = new AdoIterationService(
            _httpClient,
            _authProvider,
            config.Organization,
            config.Project,
            team);

        // Domain services — mirrors Program.cs wiring
        var protectedCacheWriter = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);

        var staleMinutes = config.Display.CacheStaleMinutes;
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            workItemRepo,
            adoService,
            protectedCacheWriter,
            pendingChangeStore,
            linkRepo,
            readOnlyStaleMinutes: staleMinutes,
            readWriteStaleMinutes: staleMinutes);

        var activeItemResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);

        var contextChangeService = new ContextChangeService(
            workItemRepo,
            adoService,
            syncCoordinatorFactory.ReadWrite,
            protectedCacheWriter,
            linkRepo);

        var workingSetService = new WorkingSetService(
            contextStore,
            workItemRepo,
            pendingChangeStore,
            iterationService,
            config.User.DisplayName);

        var statusOrchestrator = new StatusOrchestrator(
            contextStore,
            workItemRepo,
            pendingChangeStore,
            activeItemResolver,
            workingSetService,
            syncCoordinatorFactory);

        var flusher = new McpPendingChangeFlusher(workItemRepo, adoService, pendingChangeStore);

        var parentPropagationService = new ParentStatePropagationService(
            workItemRepo, adoService, processConfigProvider, protectedCacheWriter);

        var promptStateWriter = new PromptStateWriter(
            contextStore,
            workItemRepo,
            config,
            paths,
            processTypeStore);

        return new WorkspaceContext(
            key,
            config,
            paths,
            cacheStore,
            workItemRepo,
            contextStore,
            pendingChangeStore,
            adoService,
            iterationService,
            processConfigProvider,
            activeItemResolver,
            syncCoordinatorFactory,
            contextChangeService,
            statusOrchestrator,
            workingSetService,
            flusher,
            promptStateWriter,
            parentPropagationService);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var entry in _contexts.Values)
        {
            if (entry.IsValueCreated)
                entry.Value.Dispose();
        }

        _contexts.Clear();
    }
}
