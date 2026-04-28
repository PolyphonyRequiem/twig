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
/// Unit tests for <see cref="ReadTools.Workspace"/> (twig_workspace MCP tool).
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

        // Workspace field — validates acceptance criterion:
        // "twig_workspace reports the workspace associated with the active context item"
        root.GetProperty("workspace").GetString().ShouldBe("testorg/testproject");

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

    // ═══════════════════════════════════════════════════════════════
    //  Tracked items included in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_WithTrackedItems_IncludesTrackedItemsInOutput()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var trackedAt = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var tracked1 = new TrackedItem(42, Twig.Domain.Enums.TrackingMode.Single, trackedAt);
        var tracked2 = new TrackedItem(99, Twig.Domain.Enums.TrackingMode.Tree, trackedAt.AddHours(1));
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns([tracked1, tracked2]);
        _trackingRepo.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        var trackedItems = root.GetProperty("trackedItems");
        trackedItems.GetArrayLength().ShouldBe(2);
        trackedItems[0].GetProperty("workItemId").GetInt32().ShouldBe(42);
        trackedItems[0].GetProperty("mode").GetString().ShouldBe("Single");
        trackedItems[1].GetProperty("workItemId").GetInt32().ShouldBe(99);
        trackedItems[1].GetProperty("mode").GetString().ShouldBe("Tree");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Excluded items included in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_WithExcludedItems_IncludesExcludedItemsInOutput()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());

        var excludedAt = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.Zero);
        var excluded1 = new ExcludedItem(50, "noise", excludedAt);
        var excluded2 = new ExcludedItem(60, "irrelevant", excludedAt.AddDays(1));
        _trackingRepo.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns([excluded1, excluded2]);

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        var excludedItems = root.GetProperty("excludedItems");
        excludedItems.GetArrayLength().ShouldBe(2);
        excludedItems[0].GetProperty("workItemId").GetInt32().ShouldBe(50);
        excludedItems[0].GetProperty("reason").GetString().ShouldBe("noise");
        excludedItems[1].GetProperty("workItemId").GetInt32().ShouldBe(60);
        excludedItems[1].GetProperty("reason").GetString().ShouldBe("irrelevant");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty tracking — arrays present but empty
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_NoTracking_ReturnsEmptyTrackedAndExcludedArrays()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _trackingRepo.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("trackedItems").GetArrayLength().ShouldBe(0);
        root.GetProperty("excludedItems").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tracked items with trackedAt ISO 8601 format
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TrackedItem_SerializesTrackedAtAsIso8601()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var trackedAt = new DateTimeOffset(2026, 3, 10, 14, 30, 0, TimeSpan.Zero);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns([new TrackedItem(77, Twig.Domain.Enums.TrackingMode.Single, trackedAt)]);
        _trackingRepo.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        var item = root.GetProperty("trackedItems")[0];
        item.GetProperty("trackedAt").GetString().ShouldBe("2026-03-10T14:30:00.0000000+00:00");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Configured sprints — uses SprintIterationResolver
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_ConfiguredSprints_UsesResolverInsteadOfCurrentIteration()
    {
        var sprint1 = IterationPath.Parse("Project\\Sprint 1").Value;
        var sprint2 = IterationPath.Parse("Project\\Sprint 2").Value;

        var configWithSprints = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = "Test User" },
            Workspace = new WorkspaceConfig
            {
                Sprints = [new SprintEntry { Expression = "@current" }, new SprintEntry { Expression = "@current-1" }]
            },
        };

        // Setup team iterations for resolver
        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TeamIteration>
            {
                new("Project\\Sprint 1", DateTimeOffset.UtcNow.AddDays(-14), DateTimeOffset.UtcNow.AddDays(-1)),
                new("Project\\Sprint 2", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(13)),
            });
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(sprint2);

        // Items in sprint 1 (previous) and sprint 2 (current)
        var item1 = new WorkItemBuilder(10, "Old Sprint Item").AsTask().InState("Closed").AssignedTo("Test User").Build();
        var item2 = new WorkItemBuilder(20, "Current Sprint Item").AsTask().InState("Active").AssignedTo("Test User").Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(sprint1, "Test User", Arg.Any<CancellationToken>())
            .Returns([item1]);
        _workItemRepo.GetByIterationAndAssigneeAsync(sprint2, "Test User", Arg.Any<CancellationToken>())
            .Returns([item2]);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configWithSprints).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(2);

        // Should NOT have called GetCurrentIterationAsync for the fallback path
        // (it may be called by the resolver internally, but not by the old single-iteration path)
        await _workItemRepo.DidNotReceive()
            .GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workspace_ConfiguredSprints_AllTrue_FetchesAllUsers()
    {
        var sprint1 = IterationPath.Parse("Project\\Sprint 1").Value;

        var configWithSprints = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = "Test User" },
            Workspace = new WorkspaceConfig
            {
                Sprints = [new SprintEntry { Expression = "@current" }]
            },
        };

        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TeamIteration>
            {
                new("Project\\Sprint 1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14)),
            });
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(sprint1);

        var item1 = new WorkItemBuilder(10, "User A Item").AsTask().InState("Active").AssignedTo("User A").Build();
        var item2 = new WorkItemBuilder(20, "User B Item").AsTask().InState("Active").AssignedTo("User B").Build();
        _workItemRepo.GetByIterationAsync(sprint1, Arg.Any<CancellationToken>())
            .Returns([item1, item2]);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configWithSprints).Workspace(all: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(2);

        // Should have used unfiltered method (all=true)
        await _workItemRepo.Received(1)
            .GetByIterationAsync(sprint1, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive()
            .GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workspace_NoConfiguredSprints_FallsBackToCurrentIteration()
    {
        // Config with null Sprints (default)
        var configNoSprints = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = "Test User" },
            Workspace = new WorkspaceConfig { Sprints = null },
        };

        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item = new WorkItemBuilder(50, "Fallback Item").AsTask().InState("Active").AssignedTo("Test User").Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configNoSprints).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);

        // Verify fallback path used current iteration
        await _workItemRepo.Received(1)
            .GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workspace_EmptyConfiguredSprints_FallsBackToCurrentIteration()
    {
        var configEmptySprints = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = "Test User" },
            Workspace = new WorkspaceConfig { Sprints = [] },
        };

        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item = new WorkItemBuilder(51, "Fallback Item 2").AsTask().InState("Active").AssignedTo("Test User").Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configEmptySprints).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);

        await _workItemRepo.Received(1)
            .GetByIterationAndAssigneeAsync(_currentIteration, "Test User", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workspace_ConfiguredSprints_InvalidExpressionSkipped()
    {
        var sprint1 = IterationPath.Parse("Project\\Sprint 1").Value;

        var configWithBadExpr = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = "Test User" },
            Workspace = new WorkspaceConfig
            {
                // One valid, one invalid (empty expression)
                Sprints = [new SprintEntry { Expression = "@current" }, new SprintEntry { Expression = "" }]
            },
        };

        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TeamIteration>
            {
                new("Project\\Sprint 1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14)),
            });
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(sprint1);

        var item = new WorkItemBuilder(10, "Good Item").AsTask().InState("Active").AssignedTo("Test User").Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(sprint1, "Test User", Arg.Any<CancellationToken>())
            .Returns([item]);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configWithBadExpr).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        // Only the valid expression resolves, so we get items from it
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Workspace_ConfiguredSprints_AllResolveToNull_ReturnsEmptyItems()
    {
        var configWithOutOfBounds = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = "Test User" },
            Workspace = new WorkspaceConfig
            {
                // @current+99 will resolve to null (out of bounds)
                Sprints = [new SprintEntry { Expression = "@current+99" }]
            },
        };

        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TeamIteration>
            {
                new("Project\\Sprint 1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14)),
            });
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configWithOutOfBounds).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(0);
    }
}
