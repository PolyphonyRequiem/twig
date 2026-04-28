using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.Children"/> (twig_children MCP tool).
/// Covers children list, empty result, workspace field, and ADO fallback behavior.
/// </summary>
public sealed class NavigationToolsChildrenTests : NavigationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Happy path — children returned from cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Children_HappyPath_ReturnsChildrenArray()
    {
        var child1 = new WorkItemBuilder(11, "Child A").AsTask().WithParent(5).Build();
        var child2 = new WorkItemBuilder(12, "Child B").AsTask().WithParent(5).Build();

        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var result = await CreateSut().Children(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("parentId").GetInt32().ShouldBe(5);
        json.GetProperty("children").GetArrayLength().ShouldBe(2);
        json.GetProperty("count").GetInt32().ShouldBe(2);
        json.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(11);
        json.GetProperty("children")[1].GetProperty("id").GetInt32().ShouldBe(12);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No children — empty array and count zero
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Children_NoChildren_ReturnsEmptyArrayAndZeroCount()
    {
        _workItemRepo.GetChildrenAsync(99, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(99, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Children(99);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("parentId").GetInt32().ShouldBe(99);
        json.GetProperty("children").GetArrayLength().ShouldBe(0);
        json.GetProperty("count").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes workspace key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Children_Response_IncludesWorkspaceKey()
    {
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Children(1);

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO fallback — cache miss triggers ADO fetch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Children_CacheMiss_FallsBackToAdoAndReturnsChildren()
    {
        var child1 = new WorkItemBuilder(11, "ADO Child A").AsTask().WithParent(5).Build();
        var child2 = new WorkItemBuilder(12, "ADO Child B").AsTask().WithParent(5).Build();

        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var result = await CreateSut().Children(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("count").GetInt32().ShouldBe(2);
        json.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(11);
        json.GetProperty("children")[1].GetProperty("id").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task Children_CacheHit_DoesNotCallAdo()
    {
        var cached = new WorkItemBuilder(11, "Cached Child").AsTask().WithParent(5).Build();

        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { cached });

        var result = await CreateSut().Children(5);

        result.IsError.ShouldBeNull();
        await _adoService.DidNotReceive().FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        var json = ParseResult(result);
        json.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Children_CacheMissAdoFails_ReturnsEmptyList()
    {
        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network failure"));

        var result = await CreateSut().Children(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Children_CacheMissAdoCancelled_PropagatesException()
    {
        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Children(5));
    }

    [Fact]
    public async Task Children_AdoFetchedChildren_AreSavedToCache()
    {
        var child = new WorkItemBuilder(11, "ADO Child").AsTask().WithParent(5).Build();

        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        await CreateSut().Children(5);

        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(items => items.Any(i => i.Id == 11)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Children_CacheSaveFails_StillReturnsAdoResults()
    {
        var child = new WorkItemBuilder(11, "ADO Child").AsTask().WithParent(5).Build();

        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var result = await CreateSut().Children(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("count").GetInt32().ShouldBe(1);
    }
}
