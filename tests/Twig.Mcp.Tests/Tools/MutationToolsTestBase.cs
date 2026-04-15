using NSubstitute;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class MutationToolsTestBase : ContextToolsTestBase
{
    protected readonly IProcessConfigurationProvider _processConfigProvider =
        Substitute.For<IProcessConfigurationProvider>();

    protected MutationTools CreateMutationSut()
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var flusher = new McpPendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore);
        return new MutationTools(
            resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _processConfigProvider, _promptStateWriter, flusher, CreateSyncCoordinator());
    }

    /// <summary>
    /// Builds a minimal <see cref="ProcessConfiguration"/> with one work item type
    /// and the specified ordered states.
    /// </summary>
    protected static ProcessConfiguration BuildProcessConfig(
        WorkItemType type, params (string name, int order)[] states)
    {
        // Sort by order to build entries in the expected sequence
        var sorted = states.OrderBy(s => s.order).ToArray();
        var stateEntries = sorted
            .Select(s => new StateEntry(s.name, StateCategory.InProgress, null))
            .ToArray();

        var record = new ProcessTypeRecord
        {
            TypeName = type.ToString(),
            States = stateEntries,
            ValidChildTypes = Array.Empty<string>(),
        };

        return ProcessConfiguration.FromRecords(new[] { record });
    }
}
