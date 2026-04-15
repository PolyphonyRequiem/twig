using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ReadTools.Workspace"/> (twig.workspace MCP tool).
/// Covers happy path, assignee filtering, no context item, null display name fallback,
/// seeds, stale seeds, and dirty item counts.
/// </summary>
public sealed class ReadToolsWorkspaceTests : ReadToolsTestBase
{
    private readonly IterationPath _currentIteration = IterationPath.Parse("Project\\Sprint 1").Value;

    private TwigConfiguration _config = new()
    {
        Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
        Seed = new SeedConfig { StaleDays = 14 },
        User = new UserConfig { DisplayName = "Test User" },
    };

    private void SetupIteration()
    {
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(_currentIteration);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — all=false with user filter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_AllFalse_FiltersSprintItemsByUser()
    {
        SetupIteration();
        var contextItem = new WorkItemBuilder(42, "My Task").AsTask().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(contextItem);

        var sprintItem = new WorkItemBuilder(100, "Sprint Item").AsTask()
            .InState("Active").AssignedTo("Test User").Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns([sprintItem]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("context").GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);
        root.GetProperty("sprintItems")[0].GetProperty("id").GetInt32().ShouldBe(100);

        // Verify it called the assignee-filtered method
        await _workItemRepo.Received(1)
            .GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive()
            .GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  all=true — fetches all team items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_AllTrue_FetchesAllSprintItems()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item1 = new WorkItemBuilder(10, "Item A").AsTask().InState("Active").Build();
        var item2 = new WorkItemBuilder(11, "Item B").AsTask().InState("New").Build();
        _workItemRepo.GetByIterationAsync(_currentIteration, Arg.Any<CancellationToken>())
            .Returns([item1, item2]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(all: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(2);

        // Verify it called the unfiltered method
        await _workItemRepo.Received(1)
            .GetByIterationAsync(_currentIteration, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive()
            .GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Context ID exists but item not in cache — null context
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_ContextIdNotInRepo_ReturnsNullContext()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Null display name and all=false — falls back to all items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_NullDisplayNameAllFalse_FallsBackToAllItems()
    {
        _config = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = null },
        };

        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item = new WorkItemBuilder(50, "Team Item").AsTask().InState("Active").Build();
        _workItemRepo.GetByIterationAsync(_currentIteration, Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);

        // Should have used unfiltered method since display name is null
        await _workItemRepo.Received(1)
            .GetByIterationAsync(_currentIteration, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive()
            .GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seeds included in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_WithSeeds_IncludesSeedsInOutput()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var seed1 = new WorkItemBuilder(200, "Seed A").AsTask().AsSeed().Build();
        var seed2 = new WorkItemBuilder(201, "Seed B").AsTask().AsSeed().Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns([seed1, seed2]);

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(2);
        root.GetProperty("seeds")[0].GetProperty("id").GetInt32().ShouldBe(200);
        root.GetProperty("seeds")[1].GetProperty("id").GetInt32().ShouldBe(201);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dirty count in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_WithDirtyItems_ReportsDirtyCount()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var clean = new WorkItemBuilder(10, "Clean").AsTask().InState("Active").Build();
        var dirty = new WorkItemBuilder(11, "Dirty").AsTask().InState("Active").Dirty().Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns([clean, dirty]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("dirtyCount").GetInt32().ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stale seeds in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_StaleSeeds_ReportsStaleIds()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var staleSeed = new WorkItemBuilder(300, "Old Seed").AsTask().AsSeed(daysOld: 30).Build();
        var freshSeed = new WorkItemBuilder(301, "Fresh Seed").AsTask().AsSeed(daysOld: 1).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns([staleSeed, freshSeed]);

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        // staleDays = 14, so only the 30-day-old seed is stale
        var staleSeeds = root.GetProperty("staleSeeds");
        staleSeeds.GetArrayLength().ShouldBe(1);
        staleSeeds[0].GetInt32().ShouldBe(300);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty workspace — no context, sprint items, or seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_Empty_ReturnsZeroCounts()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(0);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("staleSeeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("dirtyCount").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dirty seeds contribute to dirtyCount
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_DirtySeed_CountedInDirtyCount()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var dirtySeed = new WorkItemBuilder(500, "Dirty Seed").AsTask().AsSeed().Dirty().Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns([dirtySeed]);

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("dirtyCount").GetInt32().ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Work item JSON properties serialized correctly
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_SprintItem_ContainsAllCoreProperties()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item = new WorkItemBuilder(42, "My Bug").AsBug().InState("Resolved")
            .AssignedTo("Alice").WithParent(10).Dirty().Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var sprintItem = root.GetProperty("sprintItems")[0];

        sprintItem.GetProperty("id").GetInt32().ShouldBe(42);
        sprintItem.GetProperty("title").GetString().ShouldBe("My Bug");
        sprintItem.GetProperty("type").GetString().ShouldBe("Bug");
        sprintItem.GetProperty("state").GetString().ShouldBe("Resolved");
        sprintItem.GetProperty("assignedTo").GetString().ShouldBe("Alice");
        sprintItem.GetProperty("isDirty").GetBoolean().ShouldBe(true);
        sprintItem.GetProperty("isSeed").GetBoolean().ShouldBe(false);
        sprintItem.GetProperty("parentId").GetInt32().ShouldBe(10);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed without SeedCreatedAt — not counted as stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_SeedWithoutSeedCreatedAt_NotStale()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Build a seed manually without SeedCreatedAt (isSeed=true but no date)
        var seedNoDate = new WorkItem
        {
            Id = 600,
            Title = "Dateless Seed",
            Type = WorkItemType.Task,
            State = "New",
            IsSeed = true,
            SeedCreatedAt = null,
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns([seedNoDate]);

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        root.GetProperty("staleSeeds").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Context item has null parentId serialized correctly
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_ContextItemNoParent_ParentIdIsNull()
    {
        SetupIteration();
        var contextItem = new WorkItemBuilder(7, "Root Item").AsEpic().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(contextItem);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var context = root.GetProperty("context");

        context.GetProperty("id").GetInt32().ShouldBe(7);
        context.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
