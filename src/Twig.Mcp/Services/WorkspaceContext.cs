using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Mcp.Services;

/// <summary>
/// Bundles all per-workspace services for a single <c>(org, project)</c> workspace.
/// Created by <see cref="WorkspaceContextFactory"/> and cached for the process lifetime.
/// Disposes the underlying <see cref="SqliteCacheStore"/> when the context is disposed.
/// </summary>
public sealed class WorkspaceContext : IDisposable
{
    public WorkspaceKey Key { get; }
    public TwigConfiguration Config { get; }
    public TwigPaths Paths { get; }
    public IWorkItemRepository WorkItemRepo { get; }
    public IContextStore ContextStore { get; }
    public IPendingChangeStore PendingChangeStore { get; }
    public IAdoWorkItemService AdoService { get; }
    public IIterationService IterationService { get; }
    public IProcessConfigurationProvider ProcessConfigProvider { get; }
    public ActiveItemResolver ActiveItemResolver { get; }
    public SyncCoordinatorFactory SyncCoordinatorFactory { get; }
    public ContextChangeService ContextChangeService { get; }
    public StatusOrchestrator StatusOrchestrator { get; }
    public WorkingSetService WorkingSetService { get; }
    public McpPendingChangeFlusher Flusher { get; }
    public IPromptStateWriter PromptStateWriter { get; }

    internal SqliteCacheStore CacheStore { get; }

    public WorkspaceContext(
        WorkspaceKey key,
        TwigConfiguration config,
        TwigPaths paths,
        SqliteCacheStore cacheStore,
        IWorkItemRepository workItemRepo,
        IContextStore contextStore,
        IPendingChangeStore pendingChangeStore,
        IAdoWorkItemService adoService,
        IIterationService iterationService,
        IProcessConfigurationProvider processConfigProvider,
        ActiveItemResolver activeItemResolver,
        SyncCoordinatorFactory syncCoordinatorFactory,
        ContextChangeService contextChangeService,
        StatusOrchestrator statusOrchestrator,
        WorkingSetService workingSetService,
        McpPendingChangeFlusher flusher,
        IPromptStateWriter promptStateWriter)
    {
        Key = key;
        Config = config;
        Paths = paths;
        CacheStore = cacheStore;
        WorkItemRepo = workItemRepo;
        ContextStore = contextStore;
        PendingChangeStore = pendingChangeStore;
        AdoService = adoService;
        IterationService = iterationService;
        ProcessConfigProvider = processConfigProvider;
        ActiveItemResolver = activeItemResolver;
        SyncCoordinatorFactory = syncCoordinatorFactory;
        ContextChangeService = contextChangeService;
        StatusOrchestrator = statusOrchestrator;
        WorkingSetService = workingSetService;
        Flusher = flusher;
        PromptStateWriter = promptStateWriter;
    }

    public void Dispose()
    {
        CacheStore.Dispose();
    }
}
