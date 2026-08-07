using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class MutationToolsTestBase : ReadToolsTestBase
{
    private ConnectionResolver? _sharedResolver;

    /// <summary>
    /// ONE resolver per test. <c>BuildResolver</c> constructs a fresh connection scope each call,
    /// so building it twice would put a pin in one Bench store and read from another — the pin
    /// would simply not be there, and the test would look like a behaviour bug.
    /// </summary>
    private ConnectionResolver Resolver => _sharedResolver ??= BuildResolver(DefaultConfig);

    protected MutationTools CreateMutationSut()
    {
        return new MutationTools(Resolver);
    }

    /// <summary>
    /// Places a TREE pin on the current Bench — the only pin store since ADO #146, which wiped
    /// the tracking file's pin half rather than migrating it.
    /// <para>
    /// 🔴 This writes a real pin through the real workflow rather than stubbing a tracked-set
    /// read. The refresh scope under test is "what is pinned", and a stubbed read would keep
    /// passing even if pinning stopped reaching the store the sync path consults — which is
    /// precisely the two-store drift #146 removed.
    /// </para>
    /// </summary>
    protected async Task PinTreeAsync(int workItemId)
    {
        var scope = Resolver.Resolve();
        await scope.Get<Twig.Infrastructure.Services.Mutation.PinWorkflow>()
            .PinAsync(workItemId, includeSubtree: true);
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
