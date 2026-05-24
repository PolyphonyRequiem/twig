using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;

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
    public WorkingSetService WorkingSetService { get; }
    public McpPendingChangeFlusher Flusher { get; }
    public IPromptStateWriter PromptStateWriter { get; }
    public ParentStatePropagationService ParentPropagationService { get; }
    public StateTransitionWorkflow StateTransitionWorkflow { get; }
    public FieldUpdateWorkflow FieldUpdateWorkflow { get; }
    public NoteWorkflow NoteWorkflow { get; }
    public DiscardWorkflow DiscardWorkflow { get; }
    public DeleteWorkflow DeleteWorkflow { get; }
    public PatchWorkflow PatchWorkflow { get; }
    public ITrackingRepository? TrackingRepo { get; }
    public IProcessTypeStore ProcessTypeStore { get; }
    public IFieldDefinitionStore FieldDefinitionStore { get; }

    /// <summary>
    /// Resolves sprint iteration expressions and aggregates items across multiple iterations.
    /// </summary>
    public SprintIterationResolver SprintIterationResolver { get; }

    /// <summary>
    /// Branch-to-work-item linking service. Null when git project/repository are not configured.
    /// </summary>
    public BranchLinkService? BranchLinkService { get; }

    /// <summary>
    /// Repository for virtual seed links. Used by seed publish orchestration.
    /// </summary>
    public ISeedLinkRepository SeedLinkRepo { get; }

    /// <summary>
    /// Repository for recording seed-to-ADO ID mappings after publish.
    /// </summary>
    public IPublishIdMapRepository PublishIdMapRepo { get; }

    /// <summary>
    /// Provides the publish rules that seeds are validated against.
    /// </summary>
    public ISeedPublishRulesProvider SeedPublishRulesProvider { get; }

    /// <summary>
    /// Unit of work for transactional consistency across repository operations.
    /// </summary>
    public IUnitOfWork UnitOfWork { get; }

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
        WorkingSetService workingSetService,
        McpPendingChangeFlusher flusher,
        IPromptStateWriter promptStateWriter,
        ParentStatePropagationService parentPropagationService,
        StateTransitionWorkflow stateTransitionWorkflow,
        FieldUpdateWorkflow fieldUpdateWorkflow,
        NoteWorkflow noteWorkflow,
        DiscardWorkflow discardWorkflow,
        DeleteWorkflow deleteWorkflow,
        PatchWorkflow patchWorkflow,
        SprintIterationResolver sprintIterationResolver,
        IProcessTypeStore processTypeStore,
        IFieldDefinitionStore fieldDefinitionStore,
        ISeedLinkRepository seedLinkRepo,
        IPublishIdMapRepository publishIdMapRepo,
        ISeedPublishRulesProvider seedPublishRulesProvider,
        IUnitOfWork unitOfWork,
        ITrackingRepository? trackingRepo = null,
        BranchLinkService? branchLinkService = null)
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
        WorkingSetService = workingSetService;
        Flusher = flusher;
        PromptStateWriter = promptStateWriter;
        ParentPropagationService = parentPropagationService;
        StateTransitionWorkflow = stateTransitionWorkflow;
        FieldUpdateWorkflow = fieldUpdateWorkflow;
        NoteWorkflow = noteWorkflow;
        DiscardWorkflow = discardWorkflow;
        DeleteWorkflow = deleteWorkflow;
        PatchWorkflow = patchWorkflow;
        SprintIterationResolver = sprintIterationResolver;
        ProcessTypeStore = processTypeStore;
        FieldDefinitionStore = fieldDefinitionStore;
        SeedLinkRepo = seedLinkRepo;
        PublishIdMapRepo = publishIdMapRepo;
        SeedPublishRulesProvider = seedPublishRulesProvider;
        UnitOfWork = unitOfWork;
        TrackingRepo = trackingRepo;
        BranchLinkService = branchLinkService;
    }

    /// <summary>
    /// Fetches a work item by ID: cache-first, ADO fallback, best-effort cache warm.
    /// Returns an error string (not <c>null</c>) on failure; callers wrap with <c>McpResultBuilder.ToError</c>.
    /// </summary>
    internal async Task<(WorkItem? Item, string? Error)> FetchWithFallbackAsync(int id, CancellationToken ct)
    {
        var item = await WorkItemRepo.GetByIdAsync(id, ct);
        if (item is not null) return (item, null);
        try { item = await AdoService.FetchAsync(id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return (null, $"Work item #{id} not found in cache or ADO: {ex.Message}"); }

        try { await WorkItemRepo.SaveAsync(item, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return (item, null);
    }

    /// <summary>
    /// Fetches children for <paramref name="parentId"/>: cache-first, ADO fallback on empty cache,
    /// best-effort cache warm. ADO failures are swallowed; <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    internal async Task<IReadOnlyList<WorkItem>> FetchChildrenWithFallbackAsync(int parentId, CancellationToken ct)
    {
        var children = await WorkItemRepo.GetChildrenAsync(parentId, ct);
        if (children.Count > 0) return children;

        try { children = await AdoService.FetchChildrenAsync(parentId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return []; }

        if (children.Count > 0)
        {
            try { await WorkItemRepo.SaveBatchAsync(children, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        return children;
    }

    public void Dispose()
    {
        CacheStore.Dispose();
    }
}