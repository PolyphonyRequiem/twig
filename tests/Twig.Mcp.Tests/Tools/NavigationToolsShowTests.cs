using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
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
        var text = result.Content[0].ShouldBeOfType<ModelContextProtocol.Protocol.TextContentBlock>().Text;
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
}
