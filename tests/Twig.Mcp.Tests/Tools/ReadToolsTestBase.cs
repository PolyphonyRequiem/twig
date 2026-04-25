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
    protected readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    protected readonly IAdoGitService _adoGitService = Substitute.For<IAdoGitService>();

    protected static readonly WorkspaceKey TestWorkspaceKey = new("testorg", "testproject");

    protected static readonly TwigConfiguration DefaultConfig = new()
    {
        Display = new DisplayConfig { CacheStaleMinutes = 5 },
    };

    protected WorkspaceResolver BuildResolver(TwigConfiguration config, bool includeGitService = false)
    {
        IAdoGitService? gitService = includeGitService ? _adoGitService : null;
        BranchLinkService? branchLinkService = gitService is not null
            ? new BranchLinkService(gitService, _adoService)
            : null;

        var ctx = BuildContext(TestWorkspaceKey, config,
            _contextStore, _workItemRepo, _adoService, _pendingChangeStore,
            _linkRepo, _iterationService, _processConfigProvider, _promptStateWriter,
            _trackingRepo, branchLinkService);

        var registry = Substitute.For<IWorkspaceRegistry>();
        registry.Workspaces.Returns(new[] { TestWorkspaceKey });
        registry.IsSingleWorkspace.Returns(true);

        var factory = Substitute.For<IWorkspaceContextFactory>();
        factory.GetOrCreate(Arg.Any<WorkspaceKey>()).Returns(ci =>
        {
            var k = ci.Arg<WorkspaceKey>();
            if (k == TestWorkspaceKey) return ctx;
            throw new KeyNotFoundException($"Unknown workspace: {k}");
        });

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
        IProcessConfigurationProvider ProcessConfigProvider,
        ITrackingRepository TrackingRepo);

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
                Substitute.For<IProcessConfigurationProvider>(),
                Substitute.For<ITrackingRepository>());

            var ctx = BuildContext(key, config,
                m.ContextStore, m.WorkItemRepo, m.AdoService, m.PendingChangeStore,
                m.LinkRepo, m.IterationService, m.ProcessConfigProvider, m.PromptStateWriter,
                m.TrackingRepo);

            factory.GetOrCreate(key).Returns(ctx);
            mocks[key] = m;
        }

        return (new WorkspaceResolver(registry, factory), mocks);
    }

    private static WorkspaceContext BuildContext(
        WorkspaceKey key,
        TwigConfiguration config,
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository linkRepo,
        IIterationService iterationService,
        IProcessConfigurationProvider processConfigProvider,
        IPromptStateWriter promptStateWriter,
        ITrackingRepository? trackingRepo = null,
        BranchLinkService? branchLinkService = null)
    {
        var activeItemResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var protectedWriter = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        var syncFactory = new SyncCoordinatorFactory(
            workItemRepo, adoService, protectedWriter, pendingChangeStore,
            linkRepo,
            readOnlyStaleMinutes: config.Display.CacheStaleMinutes,
            readWriteStaleMinutes: config.Display.CacheStaleMinutes);
        var contextChange = new ContextChangeService(
            workItemRepo, adoService, syncFactory.ReadWrite, protectedWriter, linkRepo);
        var workingSet = new WorkingSetService(
            contextStore, workItemRepo, pendingChangeStore, iterationService,
            config.User.DisplayName);
        var statusOrch = new StatusOrchestrator(
            contextStore, workItemRepo, pendingChangeStore, activeItemResolver,
            workingSet, syncFactory);
        var flusher = new McpPendingChangeFlusher(workItemRepo, adoService, pendingChangeStore);
        var parentPropagation = new ParentStatePropagationService(
            workItemRepo, adoService, processConfigProvider, protectedWriter);
        var paths = TwigPaths.ForContext(Path.GetTempPath(), key.Org, key.Project);
        var cacheStore = new SqliteCacheStore("Data Source=:memory:");

        return new WorkspaceContext(
            key, config, paths, cacheStore,
            workItemRepo, contextStore, pendingChangeStore,
            adoService, iterationService, processConfigProvider,
            activeItemResolver, syncFactory, contextChange,
            statusOrch, workingSet, flusher, promptStateWriter, parentPropagation,
            trackingRepo,
            branchLinkService);
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

    protected static string GetErrorText(CallToolResult result) =>
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
}
