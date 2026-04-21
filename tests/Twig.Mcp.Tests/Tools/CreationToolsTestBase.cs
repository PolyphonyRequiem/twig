using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class CreationToolsTestBase : ReadToolsTestBase
{
    protected CreationTools CreateCreationSut()
    {
        return new CreationTools(BuildResolver(DefaultConfig));
    }

    /// <summary>
    /// Builds a <see cref="ProcessConfiguration"/> with the given parent type and allowed child types.
    /// </summary>
    protected static ProcessConfiguration BuildProcessConfigWithChildren(
        WorkItemType parentType, params WorkItemType[] childTypes)
    {
        var record = new ProcessTypeRecord
        {
            TypeName = parentType.ToString(),
            States = [new StateEntry("New", Domain.Enums.StateCategory.Proposed, null)],
            ValidChildTypes = childTypes.Select(t => t.ToString()).ToArray(),
        };

        return ProcessConfiguration.FromRecords([record]);
    }
}
