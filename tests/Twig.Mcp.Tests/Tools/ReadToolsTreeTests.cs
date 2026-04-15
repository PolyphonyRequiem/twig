using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ReadTools.Tree"/> (twig.tree MCP tool).
/// Covers happy path, no active item, unreachable item, children rendering,
/// depth limiting, sibling counts, and best-effort link sync.
/// </summary>
public sealed class ReadToolsTreeTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();

    private readonly TwigConfiguration _config = new()
    {
        Display = new DisplayConfig { TreeDepth = 10, CacheStaleMinutes = 5 },
    };

    private ReadTools CreateSut()
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore,
            _linkRepo, _config.Display.CacheStaleMinutes);

        return new ReadTools(_workItemRepo, resolver, syncCoordinator, _config);
    }

    private static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateSut().Tree();

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unreachable item — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_UnreachableItem_ReturnsErrorWithId()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateSut().Tree();

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#42");
        text.ShouldContain("unreachable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — basic tree structure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_HappyPath_ReturnsFocusParentChainAndChildren()
    {
        var parent = new WorkItemBuilder(1, "Epic").AsEpic().InState("Active").Build();
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active")
            .WithParent(1).Build();
        var child1 = new WorkItemBuilder(20, "Task 1").AsTask().WithParent(10).Build();
        var child2 = new WorkItemBuilder(21, "Task 2").AsTask().WithParent(10).Build();

        SetupActiveItem(focus);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns([parent]);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns([child1, child2]);
        // Sibling count: focus has parent 1 -> siblings = children of parent 1
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns([focus]);

        var result = await CreateSut().Tree();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        // Focus
        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("focus").GetProperty("title").GetString().ShouldBe("Feature");

        // Parent chain
        var chain = root.GetProperty("parentChain");
        chain.GetArrayLength().ShouldBe(1);
        chain[0].GetProperty("id").GetInt32().ShouldBe(1);

        // Children
        var children = root.GetProperty("children");
        children.GetArrayLength().ShouldBe(2);
        children[0].GetProperty("id").GetInt32().ShouldBe(20);
        children[1].GetProperty("id").GetInt32().ShouldBe(21);

        root.GetProperty("totalChildren").GetInt32().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No parent, no children — leaf node
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_RootItemNoChildren_ReturnsEmptyArrays()
    {
        var focus = new WorkItemBuilder(5, "Solo Epic").AsEpic().InState("New").Build();

        SetupActiveItem(focus);
        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Tree();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("parentChain").GetArrayLength().ShouldBe(0);
        root.GetProperty("children").GetArrayLength().ShouldBe(0);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(0);
        root.GetProperty("links").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Depth limiting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_DepthParameter_LimitsChildren()
    {
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active").Build();
        var children = Enumerable.Range(20, 5)
            .Select(i => new WorkItemBuilder(i, $"Task {i}").AsTask().WithParent(10).Build())
            .ToList();

        SetupActiveItem(focus);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(children);

        var result = await CreateSut().Tree(depth: 2);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        // Only 2 children displayed, but totalChildren reflects actual count
        root.GetProperty("children").GetArrayLength().ShouldBe(2);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Tree_DepthNull_UsesConfigDefault()
    {
        // Config default is 10, and we have 5 children — all should be returned
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active").Build();
        var children = Enumerable.Range(20, 5)
            .Select(i => new WorkItemBuilder(i, $"Task {i}").AsTask().WithParent(10).Build())
            .ToList();

        SetupActiveItem(focus);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(children);

        var result = await CreateSut().Tree(depth: null);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("children").GetArrayLength().ShouldBe(5);
    }

    [Fact]
    public async Task Tree_DepthExceedsChildCount_ReturnsAllChildren()
    {
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active").Build();
        var children = new[]
        {
            new WorkItemBuilder(20, "Task A").AsTask().WithParent(10).Build(),
            new WorkItemBuilder(21, "Task B").AsTask().WithParent(10).Build(),
        };

        SetupActiveItem(focus);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(children);

        var result = await CreateSut().Tree(depth: 100);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("children").GetArrayLength().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Links — best-effort sync failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_LinkSyncFails_StillReturnsTree()
    {
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active").Build();

        SetupActiveItem(focus);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Make link sync throw — SyncLinksAsync calls FetchWithLinksAsync
        _adoService.FetchWithLinksAsync(10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateSut().Tree();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("links").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Auto-fetched from ADO — still builds tree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_ItemFetchedFromAdo_StillBuildsTree()
    {
        var focus = new WorkItemBuilder(42, "New Feature").AsFeature().InState("New").Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(focus);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Tree();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("focus").GetProperty("title").GetString().ShouldBe("New Feature");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Sibling counts in JSON output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tree_SiblingCounts_IncludedInJsonOutput()
    {
        var parent = new WorkItemBuilder(1, "Epic").AsEpic().InState("Active").Build();
        var focus = new WorkItemBuilder(10, "Feature").AsFeature().InState("Active")
            .WithParent(1).Build();
        var sibling = new WorkItemBuilder(11, "Sibling").AsFeature().WithParent(1).Build();

        SetupActiveItem(focus);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns([parent]);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        // Parent 1 has 2 children (focus + sibling) -> sibling count for focus = 2
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns([focus, sibling]);

        var result = await CreateSut().Tree();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);

        var siblingCounts = root.GetProperty("siblingCounts");
        // Parent (root) has null sibling count
        siblingCounts.GetProperty("1").ValueKind.ShouldBe(JsonValueKind.Null);
        // Focus has 2 siblings (children of parent)
        siblingCounts.GetProperty("10").GetInt32().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(item);
    }
}
