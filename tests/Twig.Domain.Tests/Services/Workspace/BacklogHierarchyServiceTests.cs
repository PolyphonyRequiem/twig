using Shouldly;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

// ── EPIC-4: InferParentChildMap unit tests ─────────────────────
public class BacklogHierarchyServiceTests
{
    [Fact]
    public void InferParentChildMap_AgileHierarchy_MapsCorrectly()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
                new BacklogLevelConfiguration { Name = "Features", WorkItemTypeNames = new[] { "Feature" } },
            },
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Stories", WorkItemTypeNames = new[] { "User Story" } },
            TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
        };

        var map = BacklogHierarchyService.InferParentChildMap(config);

        map["Epic"].ShouldBe(new[] { "Feature" });
        map["Feature"].ShouldBe(new[] { "User Story" });
        map["User Story"].ShouldBe(new[] { "Task" });
        map.ContainsKey("Task").ShouldBeFalse(); // leaf — no children
    }

    [Fact]
    public void InferParentChildMap_MultipleTypesPerLevel_AllParentsGetChildren()
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

        var map = BacklogHierarchyService.InferParentChildMap(config);

        map["Initiative"].ShouldBe(new[] { "Deliverable" });
        map["Scenario"].ShouldBe(new[] { "Deliverable" });
        map["Deliverable"].ShouldBe(new[] { "Task" });
    }

    [Fact]
    public void InferParentChildMap_NullConfig_ReturnsEmpty()
    {
        var map = BacklogHierarchyService.InferParentChildMap(null);
        map.ShouldBeEmpty();
    }

    [Fact]
    public void InferParentChildMap_EmptyPortfolioBacklogs_MapsRequirementToTask()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = Array.Empty<BacklogLevelConfiguration>(),
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Stories", WorkItemTypeNames = new[] { "Story" } },
            TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
        };

        var map = BacklogHierarchyService.InferParentChildMap(config);

        map["Story"].ShouldBe(new[] { "Task" });
    }

    [Fact]
    public void InferParentChildMap_NoTaskBacklog_DoesNotThrow()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
            },
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Stories", WorkItemTypeNames = new[] { "User Story" } },
            TaskBacklog = null,
        };

        var ex = Record.Exception(() => BacklogHierarchyService.InferParentChildMap(config));
        ex.ShouldBeNull();
    }

    // ── EPIC-005: GetTypeLevelMap unit tests ─────────────────────

    [Fact]
    public void GetTypeLevelMap_AgileHierarchy_MapsCorrectLevels()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
                new BacklogLevelConfiguration { Name = "Features", WorkItemTypeNames = new[] { "Feature" } },
            },
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Stories", WorkItemTypeNames = new[] { "User Story" } },
            TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
        };

        var map = BacklogHierarchyService.GetTypeLevelMap(config);

        map["Epic"].ShouldBe(0);
        map["Feature"].ShouldBe(1);
        map["User Story"].ShouldBe(2);
        map["Task"].ShouldBe(3);
    }

    [Fact]
    public void GetTypeLevelMap_NullConfig_ReturnsEmpty()
    {
        var map = BacklogHierarchyService.GetTypeLevelMap(null);
        map.ShouldBeEmpty();
    }

    [Fact]
    public void GetTypeLevelMap_MultipleTypesPerLevel_SameLevel()
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

        var map = BacklogHierarchyService.GetTypeLevelMap(config);

        map["Initiative"].ShouldBe(0);
        map["Scenario"].ShouldBe(0);
        map["Deliverable"].ShouldBe(1);
        map["Task"].ShouldBe(2);
    }

    [Fact]
    public void GetTypeLevelMap_CaseInsensitiveLookup()
    {
        var config = new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
            },
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Stories", WorkItemTypeNames = new[] { "User Story" } },
            TaskBacklog = new BacklogLevelConfiguration { Name = "Tasks", WorkItemTypeNames = new[] { "Task" } },
        };

        var map = BacklogHierarchyService.GetTypeLevelMap(config);

        map.ContainsKey("epic").ShouldBeTrue();
        map.ContainsKey("EPIC").ShouldBeTrue();
        map["epic"].ShouldBe(0);
    }
}
