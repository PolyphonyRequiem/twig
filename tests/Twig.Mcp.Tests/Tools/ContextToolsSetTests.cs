using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ContextTools.Set"/> (twig.set MCP tool).
/// Covers numeric ID resolution, pattern matching, disambiguation,
/// error paths, best-effort sync, and prompt state writes.
/// </summary>
public sealed class ContextToolsSetTests : ContextToolsTestBase
{

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — numeric ID, cached
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_Cached_SetsContextAndReturnsItem()
    {
        var item = new WorkItemBuilder(42, "My Feature").AsFeature().InState("Active").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("My Feature");

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — numeric ID, fetched from ADO
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_FetchedFromAdo_SetsContextAndReturnsItem()
    {
        var item = new WorkItemBuilder(99, "ADO Item").AsTask().InState("New").Build();
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateSut().Set("99");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(99);
        root.GetProperty("title").GetString().ShouldBe("ADO Item");

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(99, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — pattern, single match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_Pattern_SingleMatch_SetsContextAndReturnsItem()
    {
        var item = new WorkItemBuilder(10, "Login Feature").AsFeature().InState("Active").Build();
        _workItemRepo.FindByPatternAsync("Login", Arg.Any<CancellationToken>())
            .Returns(new[] { item });

        var result = await CreateSut().Set("Login");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("title").GetString().ShouldBe("Login Feature");

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pattern — multiple matches → disambiguation error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_Pattern_MultipleMatches_ReturnsDisambiguationError()
    {
        var item1 = new WorkItemBuilder(10, "Login Page").AsFeature().InState("Active").Build();
        var item2 = new WorkItemBuilder(11, "Login API").AsTask().InState("New").Build();
        _workItemRepo.FindByPatternAsync("Login", Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2 });

        var result = await CreateSut().Set("Login");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("Multiple matches");
        text.ShouldContain("#10");
        text.ShouldContain("#11");
        text.ShouldContain("Login Page");
        text.ShouldContain("Login API");

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pattern — no matches → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_Pattern_NoMatches_ReturnsError()
    {
        _workItemRepo.FindByPatternAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Set("nonexistent");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("No cached items match");
        text.ShouldContain("nonexistent");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Numeric ID — unreachable → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_NumericId_Unreachable_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateSut().Set("999");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#999");
        text.ShouldContain("unreachable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty / whitespace input → error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Set_EmptyOrWhitespace_ReturnsError(string input)
    {
        var result = await CreateSut().Set(input);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("requires an ID or title pattern");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Sync failure — best-effort, does not fail the tool call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_SyncFails_StillReturnsItem()
    {
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Active")
            .LastSyncedAt(null).Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // SyncItemSetAsync will call FetchAsync for stale items — make it fail
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Sync network failure"));

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  OperationCanceledException — propagates (not swallowed)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_Cancelled_PropagatesException()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Set("42"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Output format — verifies full work item JSON shape
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ReturnsFullWorkItemJson()
    {
        var item = new WorkItemBuilder(7, "Detailed Item")
            .AsTask()
            .InState("Active")
            .AssignedTo("Test User")
            .WithParent(3)
            .Build();
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateSut().Set("7");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(7);
        root.GetProperty("title").GetString().ShouldBe("Detailed Item");
        root.GetProperty("state").GetString().ShouldBe("Active");
        root.GetProperty("assignedTo").GetString().ShouldBe("Test User");
        root.GetProperty("parentId").GetInt32().ShouldBe(3);
        root.GetProperty("isDirty").GetBoolean().ShouldBe(false);
        root.GetProperty("isSeed").GetBoolean().ShouldBe(false);
        root.TryGetProperty("workingSet", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Disambiguation list includes state for each match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_Disambiguation_IncludesStateForEachMatch()
    {
        var items = new[]
        {
            new WorkItemBuilder(1, "Alpha").AsTask().InState("New").Build(),
            new WorkItemBuilder(2, "Alpha Beta").AsFeature().InState("Active").Build(),
            new WorkItemBuilder(3, "Alpha Gamma").AsBug().InState("Closed").Build(),
        };
        _workItemRepo.FindByPatternAsync("Alpha", Arg.Any<CancellationToken>()).Returns(items);

        var result = await CreateSut().Set("Alpha");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("[New]");
        text.ShouldContain("[Active]");
        text.ShouldContain("[Closed]");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer is called after context is set
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_PromptStateWriterCalledAfterContextSet()
    {
        var item = new WorkItemBuilder(5, "Some Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(item);

        await CreateSut().Set("5");

        Received.InOrder(() =>
        {
            _contextStore.SetActiveWorkItemIdAsync(5, Arg.Any<CancellationToken>());
            _promptStateWriter.WritePromptStateAsync();
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent chain hydration — warms cache for downstream twig.tree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ItemWithParent_HydratesParentChain()
    {
        // Leaf task with parent feature — child has recent sync to isolate from SyncCoordinator
        var recentSync = DateTimeOffset.UtcNow;
        var parentItem = new WorkItemBuilder(100, "Parent Feature").AsFeature().InState("Active")
            .LastSyncedAt(recentSync).Build();
        var childItem = new WorkItemBuilder(42, "Child Task").AsTask().InState("New")
            .WithParent(100).LastSyncedAt(recentSync).Build();

        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(childItem);

        // First GetParentChainAsync call returns empty (parent not in cache)
        // Second call (after auto-fetch) returns the parent
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(
                Array.Empty<WorkItem>(),
                new[] { parentItem });

        // Parent not in cache initially → resolver auto-fetches from ADO.
        // After resolver caches it, SyncCoordinator also looks it up — return the item.
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null, parentItem);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parentItem);

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();

        // GetParentChainAsync called twice: initial miss + post-fetch
        await _workItemRepo.Received(2).GetParentChainAsync(100, Arg.Any<CancellationToken>());

        // Parent was auto-fetched via resolver (once — sync finds it cached on second lookup)
        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_ItemWithParent_CachedChain_SkipsAutoFetch()
    {
        // Parent is already in cache — no auto-fetch needed
        // Both items have recent sync to isolate from SyncCoordinator
        var recentSync = DateTimeOffset.UtcNow;
        var parentItem = new WorkItemBuilder(100, "Parent Feature").AsFeature().InState("Active")
            .LastSyncedAt(recentSync).Build();
        var childItem = new WorkItemBuilder(42, "Child Task").AsTask().InState("New")
            .WithParent(100).LastSyncedAt(recentSync).Build();

        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(childItem);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parentItem);

        // First GetParentChainAsync call returns the parent (already cached)
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parentItem });

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();

        // GetParentChainAsync called once during hydration (parent was already cached)
        await _workItemRepo.Received(1).GetParentChainAsync(100, Arg.Any<CancellationToken>());

        // No auto-fetch since parent was already cached
        await _adoService.DidNotReceive().FetchAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_ItemWithoutParent_SkipsParentChainHydration()
    {
        // Item has no parent — no chain hydration at all
        var item = new WorkItemBuilder(42, "Root Item").AsEpic().InState("Active").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();

        // GetParentChainAsync should never be called for items without parents
        await _workItemRepo.DidNotReceive().GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ContextChangeService — invoked after setting context
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_InvokesContextChangeServiceExtendWorkingSet()
    {
        var parent = new WorkItemBuilder(100, "Parent").AsFeature().InState("Active").Build();
        var child1 = new WorkItemBuilder(201, "Child 1").AsTask().InState("New").WithParent(42).Build();
        var child2 = new WorkItemBuilder(202, "Child 2").AsTask().InState("Active").WithParent(42).Build();
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Active").WithParent(100).Build();

        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>()).Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>()).Returns(new[] { child1, child2 });

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        // Verify working set summary is included in response
        var workingSet = root.GetProperty("workingSet");
        workingSet.GetProperty("parentChainCount").GetInt32().ShouldBe(1);
        workingSet.GetProperty("childCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Set_WorkingSetSummary_ZeroCounts_WhenNoRelatives()
    {
        var item = new WorkItemBuilder(42, "Orphan Item").AsEpic().InState("Active").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Set("42");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        var workingSet = root.GetProperty("workingSet");
        workingSet.GetProperty("parentChainCount").GetInt32().ShouldBe(0);
        workingSet.GetProperty("childCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Set_ContextChangeServiceFailure_DoesNotFailToolCall()
    {
        // ExtendWorkingSetAsync internally swallows errors, but even if the
        // surrounding try-catch is needed — verify the tool still succeeds.
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Active").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        // Make SyncChildrenAsync throw to trigger a failure path in ContextChangeService
        _adoService.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unreachable"));

        var result = await CreateSut().Set("42");

        // Tool call should still succeed
        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);
    }
}
