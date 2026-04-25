using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class RefreshCommandTests : RefreshCommandTestBase
{
    private readonly RefreshCommand _cmd;

    public RefreshCommandTests()
    {
        _cmd = CreateRefreshCommand();
    }

    [Fact]
    public async Task Refresh_NoItems_ReturnsSuccess()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_FetchesAndCachesItems()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });

        var item1 = CreateWorkItem(1, "Item 1");
        var item2 = CreateWorkItem(2, "Item 2");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        // Fetch/save logic delegated to RefreshOrchestrator — verified in RefreshOrchestratorTests
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_RefreshesActiveItem()
    {
        var item1 = CreateWorkItem(1, "Sprint Item");
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var active = CreateWorkItem(42, "Active Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(active);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync();

        // Active item fetch logic delegated to RefreshOrchestrator — verified in RefreshOrchestratorTests
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_UpdatesTypeAppearances()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);

        // Verify config in-memory update
        _config.TypeAppearances.ShouldNotBeNull();
        _config.TypeAppearances.Count.ShouldBe(2);
        _config.TypeAppearances.ShouldContain(a => a.Name == "Bug" && a.Color == "CC293D");
        _config.TypeAppearances.ShouldContain(a => a.Name == "Task" && a.Color == "F2CB1D");

        // Verify config was persisted to disk
        File.Exists(_paths.ConfigPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(_paths.ConfigPath);
        content.ShouldContain("typeAppearances");
        content.ShouldContain("CC293D");

        // Verify SQLite process_types rows were saved via the mock
        await _processTypeStore.Received(2).SaveAsync(Arg.Any<ProcessTypeRecord>(), Arg.Any<CancellationToken>());
        await _processTypeStore.Received().SaveAsync(
            Arg.Is<ProcessTypeRecord>(r => r.TypeName == "Bug" && r.ColorHex == "CC293D" && r.IconId == "icon_insect"),
            Arg.Any<CancellationToken>());
        await _processTypeStore.Received().SaveAsync(
            Arg.Is<ProcessTypeRecord>(r => r.TypeName == "Task" && r.ColorHex == "F2CB1D" && r.IconId == "icon_clipboard"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithAreaPathEntries_UsesCorrectWiqlOperators()
    {
        _config.Defaults.AreaPathEntries = new List<AreaPathEntry>
        {
            new() { Path = "MyProject\\TeamA", IncludeChildren = true },
            new() { Path = "MyProject\\TeamB", IncludeChildren = false }
        };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\TeamA'") &&
                wiql.Contains("[System.AreaPath] = 'MyProject\\TeamB'") &&
                wiql.Contains(" OR ")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithSingleAreaPath_AddsAreaPathFilterToWiql()
    {
        _config.Defaults.AreaPath = "MyProject\\SingleTeam";
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'MyProject\\SingleTeam'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithoutAreaPaths_NoAreaPathFilterInWiql()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => !wiql.Contains("AreaPath")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithAreaPath_ContainingQuote_EscapesInWiql()
    {
        _config.Defaults.AreaPathEntries = new List<AreaPathEntry>
        {
            new() { Path = "My'Project\\Team", IncludeChildren = true }
        };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'My''Project\\Team'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithAreaPath_ContainingQuote_FallbackEscapesInWiql()
    {
        // Test escaping through the legacy AreaPaths fallback path
        _config.Defaults.AreaPaths = new List<string> { "My'Project\\Team" };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'My''Project\\Team'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_IncludeChildrenFalse_UsesEqualsOperator()
    {
        _config.Defaults.AreaPathEntries = new List<AreaPathEntry>
        {
            new() { Path = "MyProject\\ExactTeam", IncludeChildren = false }
        };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] = 'MyProject\\ExactTeam'") &&
                !wiql.Contains("UNDER")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ActiveItemInBatchResults_ReturnsSuccess()
    {
        // Active item ID 2 is already in WIQL results — dedup logic is in RefreshOrchestrator
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2, 3 });
        var items = new[] { CreateWorkItem(1, "A"), CreateWorkItem(2, "B"), CreateWorkItem(3, "C") };
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(2);
        _adoService.FetchChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(items[0]);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(items[1]);
        _workItemRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(items[2]);

        var result = await _cmd.ExecuteAsync();

        // Dedup logic delegated to RefreshOrchestrator — verified in RefreshOrchestratorTests
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_HydratesAncestors_WhenOrphanParentIdsExist()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 10 });
        var item10 = CreateWorkItem(10, "Child Task");
        _adoService.FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(10)),
            Arg.Any<CancellationToken>())
            .Returns(new[] { item10 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // First call returns orphan parent IDs, second call returns empty (all resolved)
        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<int>>(new[] { 5 })
                    : Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            });

        var parent5 = CreateWorkItem(5, "Parent Feature");
        _adoService.FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)),
            Arg.Any<CancellationToken>())
            .Returns(new[] { parent5 });

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Ancestor hydration delegated to RefreshOrchestrator — verified in RefreshOrchestratorTests
        await _workItemRepo.Received().GetOrphanParentIdsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_TypeStateSequencesFetchException_StderrOutputGoesViaFormatter()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Override the default to throw on type-states fetch
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkItemTypeWithStates>>(_ => throw new InvalidOperationException("network error"));

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);

        await cmd.ExecuteAsync("json");

        var stderrOutput = sw.ToString();
        stderrOutput.ShouldNotContain("\x1b[");
        stderrOutput.ShouldContain("Could not fetch type data");
    }

    [Fact]
    public async Task Refresh_ProcessConfigFetchException_StderrOutputGoesViaFormatter()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Override the default to throw on process config fetch
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns<ProcessConfigurationData>(_ => throw new InvalidOperationException("service unavailable"));

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);

        await cmd.ExecuteAsync("json");

        var stderrOutput = sw.ToString();
        stderrOutput.ShouldNotContain("\x1b[");
        stderrOutput.ShouldContain("Could not fetch type data");
    }

    [Fact]
    public async Task Refresh_DoesNotOverwriteDisplayTypeColors()
    {
        // Arrange: set custom user-specified Display.TypeColors before refresh
        _config.Display.TypeColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bug"] = "FF0000",
            ["CustomType"] = "00FF00",
        };

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Act
        var result = await _cmd.ExecuteAsync();

        // Assert: Display.TypeColors is unchanged after refresh
        result.ShouldBe(0);
        _config.Display.TypeColors.ShouldNotBeNull();
        _config.Display.TypeColors.Count.ShouldBe(2);
        _config.Display.TypeColors.ShouldContainKeyAndValue("Bug", "FF0000");
        _config.Display.TypeColors.ShouldContainKeyAndValue("CustomType", "00FF00");
    }

    [Fact]
    public async Task Refresh_UpdatesLastRefreshedAtTimestamp()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Verify last_refreshed_at was persisted after refresh
        await _contextStore.Received(1).SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── WS-021b: RefreshCommand working set sync tests ────────────

    [Fact]
    public async Task Refresh_SyncWorkingSetAsync_CalledAfterSprintItemSave()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = CreateWorkItem(1, "Sprint Item");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        // Item has stale LastSyncedAt so sync coordinator will re-fetch
        var staleItem = new WorkItem
        {
            Id = 1, Type = WorkItemType.Task, Title = "Sprint Item", State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow.AddMinutes(-60),
        };
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(staleItem);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // SyncWorkingSetAsync was called — verified by FetchAsync being called for stale items
        await _adoService.Received().FetchAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_DoesNotTriggerEviction()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = CreateWorkItem(1, "Sprint Item");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // FR-013: twig refresh does NOT trigger eviction
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_Force_StillSyncsWorkingSet()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = CreateWorkItem(1, "Sprint Item");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        // --force overrides protection for sprint items but working set sync still runs
        // (uses SaveBatchAsync for sprint items, SyncWorkingSetAsync for working set)
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    // ── Phantom dirty cleansing tests (#1335 / #1396) ─────────────

    [Fact]
    public async Task Refresh_PhantomsCleansed_LogsToStderr()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = CreateWorkItem(1, "Item");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>()).Returns(3);

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        await cmd.ExecuteAsync("json");

        var stderrOutput = sw.ToString();
        stderrOutput.ShouldContain("Cleansed 3 phantom dirty flag(s)");
    }

    [Fact]
    public async Task Refresh_ZeroPhantomsCleansed_NoStderrOutput()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = CreateWorkItem(1, "Item");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        await cmd.ExecuteAsync("json");

        var stderrOutput = sw.ToString();
        stderrOutput.ShouldNotContain("phantom dirty");
        stderrOutput.ShouldNotContain("Cleansed");
    }

    [Fact]
    public async Task Refresh_BothMetadataSyncsAreCalled()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _iterationService.Received().GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>());
        await _iterationService.Received().GetProcessConfigurationAsync(Arg.Any<CancellationToken>());
        await _iterationService.Received().GetFieldDefinitionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ProcessTypeSyncFailure_DoesNotBlockFieldDefinitionSync()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkItemTypeWithStates>>(_ => throw new InvalidOperationException("ADO type fetch failed"));

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        sw.ToString().ShouldContain("Could not fetch type data");
        await _iterationService.Received().GetFieldDefinitionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_FieldDefinitionSyncFailure_DoesNotBlockProcessTypeSync()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.ValueObjects.FieldDefinition>>(_ => throw new InvalidOperationException("ADO field fetch failed"));

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        sw.ToString().ShouldContain("Could not fetch field definitions");
        await _iterationService.Received().GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>());
        await _iterationService.Received().GetProcessConfigurationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_BothMetadataSyncsFail_ReturnsSuccessWithWarnings()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkItemTypeWithStates>>(_ => throw new InvalidOperationException("type error"));
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.ValueObjects.FieldDefinition>>(_ => throw new InvalidOperationException("field error"));

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        var stderr = sw.ToString();
        stderr.ShouldContain("Could not fetch type data");
        stderr.ShouldContain("Could not fetch field definitions");
    }

    // ── Cleanup policy wiring tests ───────────────────────────────

    [Fact]
    public async Task Refresh_CleanupPolicyOnComplete_CallsApplyCleanupPolicyAsync()
    {
        _config.Tracking.CleanupPolicy = "on-complete";
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _trackingService.ApplyCleanupPolicyAsync(
                Arg.Any<TrackingCleanupPolicy>(),
                Arg.Any<IterationPath>(),
                Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _trackingService.Received(1).ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete,
            Arg.Any<IterationPath>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_CleanupPolicyNone_DoesNotCallApplyCleanupPolicyAsync()
    {
        _config.Tracking.CleanupPolicy = "none";
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _trackingService.DidNotReceive().ApplyCleanupPolicyAsync(
            Arg.Any<TrackingCleanupPolicy>(),
            Arg.Any<IterationPath>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_CleanupRemovesItems_LogsToStderr()
    {
        _config.Tracking.CleanupPolicy = "on-complete";
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _trackingService.ApplyCleanupPolicyAsync(
                Arg.Any<TrackingCleanupPolicy>(),
                Arg.Any<IterationPath>(),
                Arg.Any<CancellationToken>())
            .Returns(2);

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        sw.ToString().ShouldContain("Auto-untracked 2 item(s) per cleanup policy");
    }

    [Fact]
    public async Task Refresh_CleanupRemovesZeroItems_NoStderrOutput()
    {
        _config.Tracking.CleanupPolicy = "on-complete";
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _trackingService.ApplyCleanupPolicyAsync(
                Arg.Any<TrackingCleanupPolicy>(),
                Arg.Any<IterationPath>(),
                Arg.Any<CancellationToken>())
            .Returns(0);

        var sw = new StringWriter();
        var cmd = CreateRefreshCommand(sw);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        sw.ToString().ShouldNotContain("Auto-untracked");
    }

}
