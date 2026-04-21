using NSubstitute;
using Shouldly;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.Children"/> (twig_children MCP tool).
/// Covers children list, empty result, and workspace field in response.
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
            .Returns(Array.Empty<Domain.Aggregates.WorkItem>());

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
            .Returns(Array.Empty<Domain.Aggregates.WorkItem>());

        var result = await CreateSut().Children(1);

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

}
