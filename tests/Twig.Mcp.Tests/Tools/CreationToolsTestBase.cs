using NSubstitute;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
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

        // AFK Task #1106: creation surfaces default the assignee from the bound ADO
        // principal's canonical uniqueName. The real connection resolves this through
        // IIterationService; tests stub it here so creation does not spuriously refuse
        // before mint when a test exercises the default assignee path.
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(("Test User", "test.user@example.com"));
    }

    protected CreationTools CreateCreationSut()
    {
        return new CreationTools(BuildResolver(DefaultConfig), new SeedFactory());
    }

    /// <summary>
    /// Creates a <see cref="CreationTools"/> SUT with <see cref="IAdoGitService"/>
    /// and <see cref="BranchLinkService"/> wired into the workspace context.
    /// </summary>
    protected CreationTools CreateCreationSutWithGitService()
    {
        return new CreationTools(BuildResolver(DefaultConfig, includeGitService: true), new SeedFactory());
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
