using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;
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
    protected readonly IProcessTypeStore _processTypeStore = Substitute.For<IProcessTypeStore>();
    protected readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
    protected readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    protected readonly IPublishIdMapRepository _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
    protected readonly ISeedPublishRulesProvider _seedPublishRulesProvider = Substitute.For<ISeedPublishRulesProvider>();
    protected readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    protected static readonly Connection TestConnection = new("testorg", "testproject");

    protected static readonly TwigConfiguration DefaultConfig = new()
    {
        Display = new DisplayConfig { CacheStaleMinutes = 5 },
    };

    protected ConnectionResolver BuildResolver(TwigConfiguration config, bool includeGitService = false)
    {
        IAdoGitService? gitService = includeGitService ? _adoGitService : null;

        var ctx = BuildContext(TestConnection, config,
            _contextStore, _workItemRepo, _adoService, _pendingChangeStore,
            _linkRepo, _iterationService, _processConfigProvider, _promptStateWriter,
            _processTypeStore, _fieldDefinitionStore,
            _seedLinkRepo, _publishIdMapRepo, _seedPublishRulesProvider, _unitOfWork,
            _trackingRepo, gitService);

        var registry = Substitute.For<IConnectionRegistry>();
        registry.Workspaces.Returns(new[] { TestConnection });
        registry.IsSingleWorkspace.Returns(true);

        var factory = Substitute.For<IConnectionScopeFactory>();
        factory.GetOrCreate(Arg.Any<Connection>()).Returns(ci =>
        {
            var k = ci.Arg<Connection>();
            if (k == TestConnection) return ctx;
            throw new KeyNotFoundException($"Unknown workspace: {k}");
        });

        return new ConnectionResolver(registry, factory);
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
        ITrackingRepository TrackingRepo,
        IProcessTypeStore ProcessTypeStore,
        IFieldDefinitionStore FieldDefinitionStore,
        ISeedLinkRepository SeedLinkRepo,
        IPublishIdMapRepository PublishIdMapRepo,
        ISeedPublishRulesProvider SeedPublishRulesProvider,
        IUnitOfWork UnitOfWork);

    /// <summary>
    /// Builds a <see cref="ConnectionResolver"/> with multiple workspaces, each backed by
    /// independent mock sets. Returns the resolver and a dictionary of per-workspace mocks
    /// for test setup.
    /// </summary>
    protected static (ConnectionResolver Resolver, IReadOnlyDictionary<Connection, WorkspaceMocks> Mocks)
        BuildMultiResolver(TwigConfiguration config, params Connection[] keys)
    {
        var mocks = new Dictionary<Connection, WorkspaceMocks>();

        var registry = Substitute.For<IConnectionRegistry>();
        registry.Workspaces.Returns(keys.ToList().AsReadOnly());
        registry.IsSingleWorkspace.Returns(keys.Length == 1);

        var factory = Substitute.For<IConnectionScopeFactory>();

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
                Substitute.For<ITrackingRepository>(),
                Substitute.For<IProcessTypeStore>(),
                Substitute.For<IFieldDefinitionStore>(),
                Substitute.For<ISeedLinkRepository>(),
                Substitute.For<IPublishIdMapRepository>(),
                Substitute.For<ISeedPublishRulesProvider>(),
                Substitute.For<IUnitOfWork>());

            var ctx = BuildContext(key, config,
                m.ContextStore, m.WorkItemRepo, m.AdoService, m.PendingChangeStore,
                m.LinkRepo, m.IterationService, m.ProcessConfigProvider, m.PromptStateWriter,
                m.ProcessTypeStore, m.FieldDefinitionStore,
                m.SeedLinkRepo, m.PublishIdMapRepo, m.SeedPublishRulesProvider, m.UnitOfWork,
                m.TrackingRepo);

            factory.GetOrCreate(key).Returns(ctx);
            mocks[key] = m;
        }

        return (new ConnectionResolver(registry, factory), mocks);
    }

    private static ConnectionScope BuildContext(
        Connection key,
        TwigConfiguration config,
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository linkRepo,
        IIterationService iterationService,
        IProcessConfigurationProvider processConfigProvider,
        IPromptStateWriter promptStateWriter,
        IProcessTypeStore processTypeStore,
        IFieldDefinitionStore fieldDefinitionStore,
        ISeedLinkRepository seedLinkRepo,
        IPublishIdMapRepository publishIdMapRepo,
        ISeedPublishRulesProvider seedPublishRulesProvider,
        IUnitOfWork unitOfWork,
        ITrackingRepository? trackingRepo = null,
        IAdoGitService? adoGitService = null)
        => TestConnectionScope.Build(
            key, config, contextStore, workItemRepo, adoService, pendingChangeStore,
            linkRepo, iterationService, processConfigProvider, promptStateWriter,
            processTypeStore, fieldDefinitionStore, seedLinkRepo, publishIdMapRepo,
            seedPublishRulesProvider, unitOfWork, trackingRepo, adoGitService);

    protected ReadTools CreateSut(TwigConfiguration config)
    {
        var res = BuildResolver(config);
        return new ReadTools(res, new NavigationTools(res));
    }

    protected static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // Auto-unwrap success envelopes: return the data property directly
        if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
            && root.TryGetProperty("data", out var d))
            return d.Clone();

        return root.Clone();
    }

    protected static JsonElement ParseEnvelope(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    protected static string GetErrorText(CallToolResult result) =>
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
}
