using NSubstitute;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class ContextToolsTestBase : ReadToolsTestBase
{
    protected readonly IPromptStateWriter _promptStateWriter = Substitute.For<IPromptStateWriter>();

    protected SyncCoordinator CreateSyncCoordinator() =>
        new(_workItemRepo, _adoService,
            new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore),
            _pendingChangeStore, _linkRepo, cacheStaleMinutes: 5);

    protected ContextChangeService CreateContextChangeService() =>
        new(_workItemRepo, _adoService, CreateSyncCoordinator(),
            new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore), _linkRepo);

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
            _promptStateWriter, CreateContextChangeService());
    }
}
