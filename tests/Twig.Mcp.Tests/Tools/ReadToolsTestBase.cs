using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class ReadToolsTestBase
{
    protected readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    protected readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    protected readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    protected readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    protected readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();
    protected readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    protected readonly IPromptStateWriter _promptStateWriter = Substitute.For<IPromptStateWriter>();
    protected readonly IProcessConfigurationProvider _processConfigProvider =
        Substitute.For<IProcessConfigurationProvider>();

    protected static readonly WorkspaceKey TestWorkspaceKey = new("testorg", "testproject");

    protected WorkspaceResolver BuildResolver(TwigConfiguration config)
    {
        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncFactory = new SyncCoordinatorFactory(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore,
            _linkRepo,
            readOnlyStaleMinutes: config.Display.CacheStaleMinutes,
            readWriteStaleMinutes: config.Display.CacheStaleMinutes);
        var contextChange = new ContextChangeService(
            _workItemRepo, _adoService, syncFactory.ReadWrite, protectedWriter, _linkRepo);
        var workingSet = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, _iterationService,
            config.User.DisplayName);
        var statusOrch = new StatusOrchestrator(
            _contextStore, _workItemRepo, _pendingChangeStore, activeItemResolver,
            workingSet, syncFactory);
        var flusher = new McpPendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore);
        var paths = TwigPaths.ForContext(Path.GetTempPath(), TestWorkspaceKey.Org, TestWorkspaceKey.Project);
        var cacheStore = new SqliteCacheStore("Data Source=:memory:");

        var ctx = new WorkspaceContext(
            TestWorkspaceKey, config, paths, cacheStore,
            _workItemRepo, _contextStore, _pendingChangeStore,
            _adoService, _iterationService, _processConfigProvider,
            activeItemResolver, syncFactory, contextChange,
            statusOrch, workingSet, flusher, _promptStateWriter);

        var registry = Substitute.For<IWorkspaceRegistry>();
        registry.Workspaces.Returns(new[] { TestWorkspaceKey });
        registry.IsSingleWorkspace.Returns(true);

        var factory = Substitute.For<IWorkspaceContextFactory>();
        factory.GetOrCreate(TestWorkspaceKey).Returns(ctx);

        return new WorkspaceResolver(registry, factory);
    }

    /// <summary>
    /// Per-workspace mock bundle for multi-workspace test scenarios.
    /// </summary>
    protected sealed record WorkspaceMocks(
        IContextStore ContextStore,
        IWorkItemRepository WorkItemRepo,
        IAdoWorkItemService AdoService,
        IPendingChangeStore PendingChangeStore,
        IWorkItemLinkRepository LinkRepo,
        IIterationService IterationService,
        IPromptStateWriter PromptStateWriter,
        IProcessConfigurationProvider ProcessConfigProvider);

    /// <summary>
    /// Builds a <see cref="WorkspaceResolver"/> with multiple workspaces, each backed by
    /// independent mock sets. Returns the resolver and a dictionary of per-workspace mocks
    /// for test setup.
    /// </summary>
    protected static (WorkspaceResolver Resolver, IReadOnlyDictionary<WorkspaceKey, WorkspaceMocks> Mocks)
        BuildMultiResolver(TwigConfiguration config, params WorkspaceKey[] keys)
    {
        var mocks = new Dictionary<WorkspaceKey, WorkspaceMocks>();

        var registry = Substitute.For<IWorkspaceRegistry>();
        registry.Workspaces.Returns(keys.ToList().AsReadOnly());
        registry.IsSingleWorkspace.Returns(keys.Length == 1);

        var factory = Substitute.For<IWorkspaceContextFactory>();

        foreach (var key in keys)
        {
            var m = new WorkspaceMocks(
                Substitute.For<IContextStore>(),
                Substitute.For<IWorkItemRepository>(),
                Substitute.For<IAdoWorkItemService>(),
                Substitute.For<IPendingChangeStore>(),
                Substitute.For<IWorkItemLinkRepository>(),
                Substitute.For<IIterationService>(),
                Substitute.For<IPromptStateWriter>(),
                Substitute.For<IProcessConfigurationProvider>());

            var activeItemResolver = new ActiveItemResolver(m.ContextStore, m.WorkItemRepo, m.AdoService);
            var protectedWriter = new ProtectedCacheWriter(m.WorkItemRepo, m.PendingChangeStore);
            var syncFactory = new SyncCoordinatorFactory(
                m.WorkItemRepo, m.AdoService, protectedWriter, m.PendingChangeStore,
                m.LinkRepo,
                readOnlyStaleMinutes: config.Display.CacheStaleMinutes,
                readWriteStaleMinutes: config.Display.CacheStaleMinutes);
            var contextChange = new ContextChangeService(
                m.WorkItemRepo, m.AdoService, syncFactory.ReadWrite, protectedWriter, m.LinkRepo);
            var workingSet = new WorkingSetService(
                m.ContextStore, m.WorkItemRepo, m.PendingChangeStore, m.IterationService,
                config.User.DisplayName);
            var statusOrch = new StatusOrchestrator(
                m.ContextStore, m.WorkItemRepo, m.PendingChangeStore, activeItemResolver,
                workingSet, syncFactory);
            var flusher = new McpPendingChangeFlusher(m.WorkItemRepo, m.AdoService, m.PendingChangeStore);
            var paths = TwigPaths.ForContext(Path.GetTempPath(), key.Org, key.Project);
            var cacheStore = new SqliteCacheStore("Data Source=:memory:");

            var ctx = new WorkspaceContext(
                key, config, paths, cacheStore,
                m.WorkItemRepo, m.ContextStore, m.PendingChangeStore,
                m.AdoService, m.IterationService, m.ProcessConfigProvider,
                activeItemResolver, syncFactory, contextChange,
                statusOrch, workingSet, flusher, m.PromptStateWriter);

            factory.GetOrCreate(key).Returns(ctx);
            mocks[key] = m;
        }

        return (new WorkspaceResolver(registry, factory), mocks);
    }

    protected ReadTools CreateSut(TwigConfiguration config)
    {
        return new ReadTools(BuildResolver(config));
    }

    protected static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
