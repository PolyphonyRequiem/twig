using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
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

    protected ReadTools CreateSut(TwigConfiguration config)
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore,
            _linkRepo, config.Display.CacheStaleMinutes);

        return new ReadTools(_workItemRepo, _contextStore, _iterationService, resolver, syncCoordinator, config);
    }

    protected static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
