using NSubstitute;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class CreationToolsTestBase : ReadToolsTestBase
{
    protected CreationToolsTestBase()
    {
        // Default process config with common types so unparented creation passes validation.
        // Parented tests override this with specific parent/child configs.
        var defaultConfig = BuildProcessConfigWithTypes(
            WorkItemType.Epic, WorkItemType.Issue, WorkItemType.Task, WorkItemType.Bug);
        _processConfigProvider.GetConfiguration().Returns(defaultConfig);
    }

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

    /// <summary>
    /// Builds a <see cref="ProcessConfiguration"/> with the given types as top-level entries (no child relationships).
    /// </summary>
    protected static ProcessConfiguration BuildProcessConfigWithTypes(params WorkItemType[] types)
    {
        var records = types.Select(t => new ProcessTypeRecord
        {
            TypeName = t.ToString(),
            States = [new StateEntry("New", Domain.Enums.StateCategory.Proposed, null)],
            ValidChildTypes = [],
        }).ToArray();

        return ProcessConfiguration.FromRecords(records);
    }
}
