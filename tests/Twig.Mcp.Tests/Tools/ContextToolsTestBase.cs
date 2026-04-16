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

    protected ContextTools CreateSut()
    {
        var cacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var sync = new SyncCoordinator(_workItemRepo, _adoService, cacheWriter, _pendingChangeStore, _linkRepo, cacheStaleMinutes: 5);
        var syncFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, cacheWriter, _pendingChangeStore, _linkRepo, readOnlyStaleMinutes: 5, readWriteStaleMinutes: 5);
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var contextChange = new ContextChangeService(_workItemRepo, _adoService, sync, cacheWriter, _linkRepo);
        var statusOrch = new StatusOrchestrator(_contextStore, _workItemRepo, _pendingChangeStore, resolver,
            new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null), syncFactory);
        return new ContextTools(_workItemRepo, _contextStore, resolver, statusOrch, _promptStateWriter, contextChange);
    }
}
