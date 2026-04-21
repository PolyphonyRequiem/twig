using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using System.Text.Json;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.Parent"/> (twig_parent MCP tool).
/// Covers cache-hit resolution, ADO fallback for child and parent, root item (no parent),
/// and best-effort parent fetch failure.
/// </summary>
public sealed class NavigationToolsParentTests : NavigationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Both child and parent in cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_BothInCache_ReturnsChildAndParent()
    {
        var parent = new WorkItemBuilder(1, "Epic Parent").AsEpic().InState("Active").Build();
        var child = new WorkItemBuilder(10, "Child Task").AsTask().WithParent(1).Build();

        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await CreateSut().Parent(10);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("child").GetProperty("id").GetInt32().ShouldBe(10);
        json.GetProperty("parent").GetProperty("id").GetInt32().ShouldBe(1);
        json.GetProperty("parent").GetProperty("title").GetString().ShouldBe("Epic Parent");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Root item — parent is null in response
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_RootItem_ReturnsNullParent()
    {
        var root = new WorkItemBuilder(5, "Root Epic").AsEpic().Build(); // no parentId
        _workItemRepo.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(root);

        var result = await CreateSut().Parent(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("child").GetProperty("id").GetInt32().ShouldBe(5);
        json.GetProperty("parent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Child not in cache → fetched from ADO
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_ChildCacheMiss_FetchesFromAdo()
    {
        var child = new WorkItemBuilder(20, "ADO Child").AsTask().WithParent(3).Build();
        var parent = new WorkItemBuilder(3, "Parent Issue").AsIssue().Build();

        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await CreateSut().Parent(20);

        result.IsError.ShouldBeNull();
        await _adoService.Received(1).FetchAsync(20, Arg.Any<CancellationToken>());
        ParseResult(result).GetProperty("child").GetProperty("id").GetInt32().ShouldBe(20);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Child not in cache + ADO failure → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_ChildCacheMissAndAdoFails_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateSut().Parent(99);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Text.ShouldContain("#99");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent not in cache → fetched from ADO (best-effort)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_ParentCacheMiss_FetchesParentFromAdo()
    {
        var child = new WorkItemBuilder(30, "Child").AsTask().WithParent(8).Build();
        var parent = new WorkItemBuilder(8, "ADO Parent").AsEpic().Build();

        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetByIdAsync(8, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);
        _adoService.FetchAsync(8, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await CreateSut().Parent(30);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("parent").GetProperty("id").GetInt32().ShouldBe(8);
        await _adoService.Received(1).FetchAsync(8, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent fetch fails → returns null parent (best-effort)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_ParentFetchFails_ReturnsNullParentBestEffort()
    {
        var child = new WorkItemBuilder(40, "Child").AsTask().WithParent(9).Build();

        _workItemRepo.GetByIdAsync(40, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);
        _adoService.FetchAsync(9, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO error"));

        var result = await CreateSut().Parent(40);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("child").GetProperty("id").GetInt32().ShouldBe(40);
        json.GetProperty("parent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes workspace key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_Response_IncludesWorkspaceKey()
    {
        var child = new WorkItemBuilder(50, "Child").AsTask().Build();
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(child);

        var result = await CreateSut().Parent(50);

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  OperationCanceledException propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parent_Cancelled_PropagatesException()
    {
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns((Domain.Aggregates.WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Parent(1));
    }
}
