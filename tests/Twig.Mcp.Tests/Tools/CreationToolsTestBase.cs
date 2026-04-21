using NSubstitute;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class CreationToolsTestBase : MutationToolsTestBase
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

    protected static ProcessConfiguration BuildProcessConfigWithChildren(
        WorkItemType parentType, params WorkItemType[] childTypes) =>
        ProcessConfiguration.FromRecords([MakeTypeRecord(parentType, childTypes)]);

    protected static ProcessConfiguration BuildProcessConfigWithTypes(params WorkItemType[] types) =>
        ProcessConfiguration.FromRecords(types.Select(t => MakeTypeRecord(t)).ToArray());

    private static ProcessTypeRecord MakeTypeRecord(WorkItemType type, params WorkItemType[] children) =>
        new()
        {
            TypeName = type.ToString(),
            States = [new StateEntry("New", Domain.Enums.StateCategory.Proposed, null)],
            ValidChildTypes = children.Select(t => t.ToString()).ToArray(),
        };
}
