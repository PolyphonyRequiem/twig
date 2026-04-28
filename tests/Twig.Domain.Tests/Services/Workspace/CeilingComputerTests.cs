using Shouldly;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

// ── EPIC-1: CeilingComputer unit tests ─────────────────────
public class CeilingComputerTests
{
    private static ProcessConfigurationData AgileConfig() => new()
    {
        PortfolioBacklogs = new[]
        {
            new BacklogLevelConfiguration { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
            new BacklogLevelConfiguration { Name = "Features", WorkItemTypeNames = new[] { "Feature" } },
        },
        RequirementBacklog = new BacklogLevelConfiguration { Name = "Stories", WorkItemTypeNames = new[] { "User Story" } },
        TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
    };

    [Fact]
    public void Compute_TasksAndUserStories_ReturnsFeature()
    {
        var result = CeilingComputer.Compute(new[] { "Task", "User Story" }, AgileConfig());
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "Feature" });
    }

    [Fact]
    public void Compute_TasksOnly_ReturnsUserStory()
    {
        var result = CeilingComputer.Compute(new[] { "Task" }, AgileConfig());
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "User Story" });
    }

    [Fact]
    public void Compute_FeaturesOnly_ReturnsEpic()
    {
        var result = CeilingComputer.Compute(new[] { "Feature" }, AgileConfig());
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "Epic" });
    }

    [Fact]
    public void Compute_EpicsOnly_ReturnsNull()
    {
        var result = CeilingComputer.Compute(new[] { "Epic" }, AgileConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Compute_MixedLevels_TaskAndFeature_ReturnsEpic()
    {
        var result = CeilingComputer.Compute(new[] { "Task", "Feature" }, AgileConfig());
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "Epic" });
    }

    [Fact]
    public void Compute_NullConfig_ReturnsNull()
    {
        var result = CeilingComputer.Compute(new[] { "Task" }, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void Compute_EmptySprintItems_ReturnsNull()
    {
        var result = CeilingComputer.Compute(Array.Empty<string>(), AgileConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Compute_NullSprintItems_ReturnsNull()
    {
        var result = CeilingComputer.Compute(null, AgileConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Compute_CustomProcessTypes_ReturnsCorrectCeiling()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration { Name = "Initiatives", WorkItemTypeNames = new[] { "Initiative" } },
                new BacklogLevelConfiguration { Name = "Capabilities", WorkItemTypeNames = new[] { "Capability" } },
            },
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Requirements", WorkItemTypeNames = new[] { "Requirement" } },
            TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
        };

        var result = CeilingComputer.Compute(new[] { "Task" }, config);
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "Requirement" });
    }

    [Fact]
    public void Compute_NoMatchingTypesInHierarchy_ReturnsNull()
    {
        var result = CeilingComputer.Compute(new[] { "Bug" }, AgileConfig());
        result.ShouldBeNull();
    }

    [Fact]
    public void Compute_CaseInsensitiveMatching()
    {
        var result = CeilingComputer.Compute(new[] { "task" }, AgileConfig());
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "User Story" });
    }

    [Fact]
    public void Compute_MultipleTypesPerLevel_ReturnsAllTypeNames()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration { Name = "Top", WorkItemTypeNames = new[] { "Initiative", "Scenario" } },
            },
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Requirements", WorkItemTypeNames = new[] { "Deliverable" } },
            TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
        };

        var result = CeilingComputer.Compute(new[] { "Deliverable" }, config);
        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "Initiative", "Scenario" });
    }
}
