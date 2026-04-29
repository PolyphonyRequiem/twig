using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.Show"/> (twig_show MCP tool).
/// Covers cache hit, ADO fallback, cache miss + ADO failure, and workspace error paths.
/// </summary>
public sealed class NavigationToolsShowTests : NavigationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Cache hit — returns item without ADO call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_CacheHit_ReturnsItemWithoutAdoCall()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await CreateSut().Show(42);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("id").GetInt32().ShouldBe(42);
        json.GetProperty("title").GetString().ShouldBe("My Task");
        json.GetProperty("state").GetString().ShouldBe("Doing");
        json.GetProperty("type").GetString().ShouldBe("Task");

        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache miss → ADO fallback → saves to cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_CacheMiss_FetchesFromAdoAndCaches()
    {
        var item = new WorkItemBuilder(99, "ADO Item").AsEpic().InState("New").Build();

        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await CreateSut().Show(99);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("id").GetInt32().ShouldBe(99);

        await _workItemRepo.Received(1).SaveAsync(item, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache miss + ADO failure → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_CacheMissAdoFails_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateSut().Show(7);

        result.IsError.ShouldBe(true);
        var text = GetErrorText(result);
        text.ShouldContain("#7");
        text.ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Does NOT change active context (no twig_set side effect)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_DoesNotModifyActiveContext()
    {
        var item = new WorkItemBuilder(55, "No Context Change").AsTask().Build();
        _workItemRepo.GetByIdAsync(55, Arg.Any<CancellationToken>())
            .Returns(item);

        await CreateSut().Show(55);

        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes areaPath, iterationPath, workspace fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ResponseIncludesPathsAndWorkspace()
    {
        var item = new WorkItemBuilder(10, "Path Item").AsTask()
            .WithAreaPath("Twig\\Core")
            .WithIterationPath("Twig\\Sprint 1")
            .Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await CreateSut().Show(10);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("areaPath").GetString().ShouldBe("Twig\\Core");
        json.GetProperty("iterationPath").GetString().ShouldBe("Twig\\Sprint 1");
        json.GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Item with parent — parentId is non-null in response
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ItemWithParent_ResponseContainsParentId()
    {
        var item = new WorkItemBuilder(20, "Child Task").AsTask().WithParent(5).Build();
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await CreateSut().Show(20);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("parentId").GetInt32().ShouldBe(5);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Item with extra fields — fields object included
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ItemWithFields_ResponseContainsFieldsObject()
    {
        var item = new WorkItemBuilder(30, "With Fields").AsTask()
            .WithField("System.Description", "<p>Hello</p>")
            .Build();
        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await CreateSut().Show(30);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.TryGetProperty("fields", out var fields).ShouldBeTrue();
        fields.GetProperty("System.Description").GetString().ShouldBe("<p>Hello</p>");
    }

    // ═══════════════════════════════════════════════════════════════
    //  OperationCanceledException propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_CancellationRequested_PropagatesException()
    {
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Show(1));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes — active item includes pendingChanges array
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ActiveItemWithPendingChanges_IncludesPendingChangesArray()
    {
        var item = new WorkItemBuilder(42, "Active Task").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var changes = new List<PendingChangeRecord>
        {
            new(42, "set_field", "System.Title", "Old Title", "Active Task"),
            new(42, "add_note", null, null, "A note"),
        };
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(changes);

        var result = await CreateSut().Show(42);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.TryGetProperty("pendingChanges", out var pending).ShouldBeTrue();
        pending.GetArrayLength().ShouldBe(2);

        var first = pending[0];
        first.GetProperty("workItemId").GetInt32().ShouldBe(42);
        first.GetProperty("changeType").GetString().ShouldBe("set_field");
        first.GetProperty("fieldName").GetString().ShouldBe("System.Title");
        first.GetProperty("oldValue").GetString().ShouldBe("Old Title");
        first.GetProperty("newValue").GetString().ShouldBe("Active Task");

        var second = pending[1];
        second.GetProperty("changeType").GetString().ShouldBe("add_note");
        second.GetProperty("fieldName").GetString().ShouldBe("");
        second.GetProperty("newValue").GetString().ShouldBe("A note");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes — non-active item omits pendingChanges
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_NonActiveItem_OmitsPendingChanges()
    {
        var item = new WorkItemBuilder(42, "Some Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(99); // Different active item

        var result = await CreateSut().Show(42);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.TryGetProperty("pendingChanges", out _).ShouldBeFalse();

        // Should NOT have queried pending changes for non-active item
        await _pendingChangeStore.DidNotReceive()
            .GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes — no active item (null context) omits array
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_NoActiveContext_OmitsPendingChanges()
    {
        var item = new WorkItemBuilder(42, "Some Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateSut().Show(42);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.TryGetProperty("pendingChanges", out _).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes — active item with empty changes returns empty array
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ActiveItemWithNoPendingChanges_ReturnsEmptyArray()
    {
        var item = new WorkItemBuilder(42, "Clean Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<PendingChangeRecord>());

        var result = await CreateSut().Show(42);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.TryGetProperty("pendingChanges", out var pending).ShouldBeTrue();
        pending.GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true — returns tree hierarchy instead of detail card
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithTreeTrue_ReturnsTreeHierarchy()
    {
        var parent = new WorkItemBuilder(1, "Epic").AsEpic().InState("Active").Build();
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active")
            .WithParent(1).Build();
        var child1 = new WorkItemBuilder(20, "Task 1").AsTask().WithParent(10).Build();
        var child2 = new WorkItemBuilder(21, "Task 2").AsTask().WithParent(10).Build();

        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(focus);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns([parent]);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns([child1, child2]);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns([focus]);

        var result = await CreateSut().Show(10, tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        // Should have tree structure, not detail card
        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("focus").GetProperty("title").GetString().ShouldBe("Feature");
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(1);
        root.GetProperty("parentChain")[0].GetProperty("id").GetInt32().ShouldBe(1);
        root.GetProperty("children").GetArrayLength().ShouldBe(2);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=false (default) — returns detail card unchanged
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithTreeFalse_ReturnsDetailCard()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);

        var result = await CreateSut().Show(42, tree: false);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("id").GetInt32().ShouldBe(42);
        json.GetProperty("title").GetString().ShouldBe("My Task");
        // Should NOT have tree structure properties
        json.TryGetProperty("focus", out _).ShouldBeFalse();
        json.TryGetProperty("parentChain", out _).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true with depth — respects max depth
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithTreeAndDepth_RespectsMaxDepth()
    {
        var focus = new WorkItemBuilder(10, "Epic").AsEpic().InState("Active").Build();
        var child = new WorkItemBuilder(20, "Feature").AsFeature().WithParent(10).Build();
        var grandchild = new WorkItemBuilder(30, "Task").AsTask().WithParent(20).Build();

        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(focus);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns([child]);
        _workItemRepo.GetChildrenAsync(20, Arg.Any<CancellationToken>())
            .Returns([grandchild]);

        // depth=1: should fetch children but NOT grandchildren (depth-1 = 0 for descendants)
        var result = await CreateSut().Show(10, tree: true, depth: 1);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("children").GetArrayLength().ShouldBe(1);
        root.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(20);

        // Grandchildren should NOT have been fetched (depth=1 means descendants go 0 levels deep)
        // The descendants dict only populates at depth > 0
        await _workItemRepo.DidNotReceive()
            .GetChildrenAsync(20, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  tree=true with root item (no parent) — empty parent chain
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithTreeTrue_RootItem_ReturnsEmptyParentChain()
    {
        var focus = new WorkItemBuilder(5, "Solo Epic").AsEpic().InState("New").Build();

        _workItemRepo.GetByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(focus);
        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Show(5, tree: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(5);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(0);
        root.GetProperty("children").GetArrayLength().ShouldBe(0);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(0);
    }
}
