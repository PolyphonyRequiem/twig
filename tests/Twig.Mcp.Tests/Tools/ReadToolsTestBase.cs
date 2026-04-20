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
