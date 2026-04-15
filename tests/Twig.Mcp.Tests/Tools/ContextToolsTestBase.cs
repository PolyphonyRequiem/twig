using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class ContextToolsTestBase
{
    protected readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    protected readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    protected readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    protected readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    protected readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();
    protected readonly IPromptStateWriter _promptStateWriter = Substitute.For<IPromptStateWriter>();
    protected readonly IIterationService _iterationService = Substitute.For<IIterationService>();

    protected SyncCoordinator CreateSyncCoordinator() =>
        new(_workItemRepo, _adoService,
            new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore),
            _pendingChangeStore, _linkRepo, cacheStaleMinutes: 5);

    protected StatusOrchestrator CreateStatusOrchestrator(ActiveItemResolver resolver) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, resolver,
            new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null),
            CreateSyncCoordinator());

    protected ContextTools CreateSut()
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        return new ContextTools(
            _workItemRepo, _contextStore, resolver,
            CreateSyncCoordinator(), CreateStatusOrchestrator(resolver),
            _promptStateWriter);
    }

    protected static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
