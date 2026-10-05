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
        // ADO #1106: self-scoped reads bind to the authenticated connection's canonical identity.
        // "Test User" is used as the canonical UPN so legacy fixture items whose only assignee
        // signal is the authored display string still fall through the canonical narrowing.
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>(("Test User", "Test User")));
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
            .InState("Active").AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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

        // The self scoping now loads unfiltered rows via GetByIterationsAsync and narrows in
        // memory by the bound canonical identity.
        await _workItemRepo.Received().GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Self with no bound canonical identity — refuses rather than widening
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_WithoutBoundCanonicalIdentity_RefusesSelfView()
    {
        _config = new TwigConfiguration
        {
            Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
            Seed = new SeedConfig { StaleDays = 14 },
            User = new UserConfig { DisplayName = null },
        };

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(_currentIteration);
        // No canonical identity: the connection resolves (display, null).
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>(("Test User", null)));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateSut(_config).Workspace(all: false);

        // The self view refuses with the standard refusal message; it does NOT widen to the team.
        result.IsError.ShouldBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe(Twig.Domain.Services.Workspace.DefaultBenchSelectors.MissingBoundIdentityMessage);
        // The sprint-items path is never reached when the self principal is unresolved.
        await _workItemRepo.DidNotReceive().GetByIterationsAsync(
            Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seeds included in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_WithSeeds_IncludesSeedsInOutput()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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

        var clean = new WorkItemBuilder(10, "Clean").AsTask().InState("Active")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        var dirty = new WorkItemBuilder(11, "Dirty").AsTask().InState("Active")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Dirty().Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Rendering-only shape check: the fixture item is authored to a different principal,
        // and the all=true request opts out of the self narrowing so the serialization path is
        // the thing under test (not the bench filter).
        var result = await CreateSut(_config).Workspace(all: true);

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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
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
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>(("Test User", "Test User")));

        // Items in sprint 1 (previous) and sprint 2 (current). Both carry the canonical identity
        // through the `AssignedToUniqueName ?? AssignedTo` fallback — the row's authored assignee
        // matches the bound principal exactly.
        var item1 = new WorkItemBuilder(10, "Old Sprint Item").AsTask().InState("Closed")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        var item2 = new WorkItemBuilder(20, "Current Sprint Item").AsTask().InState("Active")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2 });

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configWithSprints).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(2);

        // Canonical scoping NEVER delegates to the display-name repo filter.
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        var item = new WorkItemBuilder(50, "Fallback Item").AsTask().InState("Active")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configNoSprints).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);

        // Verify fallback path used current iteration
        await _workItemRepo.Received().GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>());
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

        var item = new WorkItemBuilder(51, "Fallback Item 2").AsTask().InState("Active")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([item]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configEmptySprints).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);

        await _workItemRepo.Received().GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>());
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
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>(("Test User", "Test User")));

        var item = new WorkItemBuilder(10, "Good Item").AsTask().InState("Active")
            .AssignedTo("Test User").AssignedToUniqueName("Test User").Build();
        _workItemRepo.GetByIterationAsync(sprint1, Arg.Any<CancellationToken>())
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
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>(("Test User", "Test User")));

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(configWithOutOfBounds).Workspace(all: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true — returns tree-structured JSON
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_ReturnsTreeStructuredJson()
    {
        SetupIteration();
        var contextItem = new WorkItemBuilder(42, "My Task").AsTask().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(contextItem);

        var epic = new WorkItemBuilder(100, "Epic A").AsEpic().InState("Active").Build();
        var child = new WorkItemBuilder(101, "Issue A").AsIssue().InState("Active").WithParent(100).Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([epic]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns([child]);
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns([child]);
        _workItemRepo.GetChildrenAsync(101, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(101, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Tree-shape proof: the sprint item carries no explicit canonical assignee and the
        // self narrowing would hide it. `all: true` opts into the team view so the parent/child
        // assertions below exercise the tree builder, not the Bench filter.
        var result = await CreateSut(_config).Workspace(all: true, tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("mode").GetString().ShouldBe("tree");
        root.GetProperty("workspace").GetString().ShouldBe("testorg/testproject");
        root.GetProperty("context").GetProperty("id").GetInt32().ShouldBe(42);

        var roots = root.GetProperty("roots");
        roots.GetArrayLength().ShouldBe(1);
        roots[0].GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(100);
        roots[0].GetProperty("children").GetArrayLength().ShouldBe(1);
        roots[0].GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(101);
        roots[0].GetProperty("totalChildren").GetInt32().ShouldBe(1);

        root.GetProperty("totalItems").GetInt32().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true with no sprint items — empty roots
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_NoSprintItems_ReturnsEmptyRoots()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("mode").GetString().ShouldBe("tree");
        root.GetProperty("roots").GetArrayLength().ShouldBe(0);
        root.GetProperty("totalItems").GetInt32().ShouldBe(0);
        root.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true includes seeds in output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_IncludesSeedsInOutput()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var seed = new WorkItemBuilder(200, "Seed A").AsTask().AsSeed().Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns([seed]);

        var result = await CreateSut(_config).Workspace(tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        root.GetProperty("seeds")[0].GetProperty("id").GetInt32().ShouldBe(200);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=false still returns flat workspace (unchanged)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeFalse_ReturnsFlatWorkspace()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(tree: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        // Flat workspace doesn't have "mode" or "roots" — it has "sprintItems"
        root.TryGetProperty("mode", out _).ShouldBeFalse();
        root.TryGetProperty("roots", out _).ShouldBeFalse();
        root.TryGetProperty("sprintItems", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true with multiple sprint items — multiple roots
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_MultipleSprintItems_ReturnsMultipleRoots()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item1 = new WorkItemBuilder(10, "Epic A").AsEpic().InState("Active").Build();
        var item2 = new WorkItemBuilder(20, "Epic B").AsEpic().InState("Active").Build();
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([item1, item2]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Tree-shape proof: the roots are unassigned epics and the self narrowing would hide
        // them. `all: true` requests the explicit team view so the multiple-roots assertion
        // measures the tree builder, not the Bench filter.
        var result = await CreateSut(_config).Workspace(all: true, tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("roots").GetArrayLength().ShouldBe(2);
        root.GetProperty("roots")[0].GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("roots")[1].GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(20);
        root.GetProperty("totalItems").GetInt32().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true with deep hierarchy — parent chain + nested children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_DeepHierarchy_VerifiesParentChildRelationships()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Sprint item is a story with parent epic and two child tasks
        var story = new WorkItemBuilder(50, "Story X").AsUserStory().InState("Active")
            .WithParent(500).Build();
        var parentEpic = new WorkItemBuilder(500, "Parent Epic").AsEpic().InState("Active").Build();
        var childTask1 = new WorkItemBuilder(51, "Task 1").AsTask().InState("Active")
            .WithParent(50).Build();
        var childTask2 = new WorkItemBuilder(52, "Task 2").AsTask().InState("New")
            .WithParent(50).Build();
        var grandchild = new WorkItemBuilder(53, "Subtask A").AsTask().InState("New")
            .WithParent(51).Build();

        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([story]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(500, Arg.Any<CancellationToken>())
            .Returns([parentEpic]);
        _workItemRepo.GetChildrenAsync(50, Arg.Any<CancellationToken>())
            .Returns([childTask1, childTask2]);
        _adoService.FetchChildrenAsync(50, Arg.Any<CancellationToken>())
            .Returns([childTask1, childTask2]);
        _workItemRepo.GetChildrenAsync(51, Arg.Any<CancellationToken>())
            .Returns([grandchild]);
        _adoService.FetchChildrenAsync(51, Arg.Any<CancellationToken>())
            .Returns([grandchild]);
        _workItemRepo.GetChildrenAsync(52, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(52, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(53, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(53, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Tree-shape proof: the story and its hierarchy are unassigned and the self narrowing
        // would hide the sprint item. `all: true` opts into the team view so the parent/child
        // assertions below exercise the tree builder.
        var result = await CreateSut(_config).Workspace(all: true, tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("mode").GetString().ShouldBe("tree");
        root.GetProperty("roots").GetArrayLength().ShouldBe(1);

        var treeRoot = root.GetProperty("roots")[0];

        // Focus is the sprint item (story)
        treeRoot.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(50);
        treeRoot.GetProperty("focus").GetProperty("title").GetString().ShouldBe("Story X");

        // Children are present with nested grandchild
        var children = treeRoot.GetProperty("children");
        children.GetArrayLength().ShouldBe(2);
        children[0].GetProperty("id").GetInt32().ShouldBe(51);
        children[1].GetProperty("id").GetInt32().ShouldBe(52);

        // Grandchild nested under first child
        var grandchildren = children[0].GetProperty("children");
        grandchildren.GetArrayLength().ShouldBe(1);
        grandchildren[0].GetProperty("id").GetInt32().ShouldBe(53);
        grandchildren[0].GetProperty("title").GetString().ShouldBe("Subtask A");

        // Second child has no children
        children[1].GetProperty("children").GetArrayLength().ShouldBe(0);

        treeRoot.GetProperty("totalChildren").GetInt32().ShouldBe(2);

        // Total items includes focus + totalChildren (2)
        root.GetProperty("totalItems").GetInt32().ShouldBeGreaterThanOrEqualTo(3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true with all=true — shows all team items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_AllTrue_ShowsAllTeamItems()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item1 = new WorkItemBuilder(10, "Alice Task").AsTask().InState("Active")
            .AssignedTo("Alice").Build();
        var item2 = new WorkItemBuilder(20, "Bob Task").AsTask().InState("Active")
            .AssignedTo("Bob").Build();
        _workItemRepo.GetByIterationAsync(_currentIteration, Arg.Any<CancellationToken>())
            .Returns([item1, item2]);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(all: true, tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("mode").GetString().ShouldBe("tree");
        root.GetProperty("roots").GetArrayLength().ShouldBe(2);

        // Verify all=true used unfiltered method
        await _workItemRepo.Received(1)
            .GetByIterationAsync(_currentIteration, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive()
            .GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true — workspace field present
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Workspace_TreeTrue_IncludesWorkspaceField()
    {
        SetupIteration();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut(_config).Workspace(tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("workspace").GetString().ShouldBe("testorg/testproject");
    }
}
