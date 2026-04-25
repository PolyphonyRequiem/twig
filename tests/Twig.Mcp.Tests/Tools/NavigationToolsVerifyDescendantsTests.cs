using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.VerifyDescendants"/> (twig_verify_descendants MCP tool).
/// Covers verified/unverified results, default maxDepth, workspace field, and workspace resolution error.
/// </summary>
public sealed class NavigationToolsVerifyDescendantsTests : NavigationToolsTestBase
{
    public NavigationToolsVerifyDescendantsTests()
    {
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — all descendants verified (terminal state)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyDescendants_AllTerminal_ReturnsVerifiedTrue()
    {
        var child = new WorkItemBuilder(11, "Child A").AsTask().InState("Done").WithParent(5).Build();
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().VerifyDescendants(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("rootId").GetInt32().ShouldBe(5);
        json.GetProperty("verified").GetBoolean().ShouldBeTrue();
        json.GetProperty("totalChecked").GetInt32().ShouldBe(1);
        json.GetProperty("incompleteCount").GetInt32().ShouldBe(0);
        json.GetProperty("incomplete").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Incomplete descendants — verified false with items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyDescendants_IncompleteDescendant_ReturnsVerifiedFalseWithItems()
    {
        var child = new WorkItemBuilder(11, "Active Child").AsTask().InState("Doing").WithParent(5).Build();
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().VerifyDescendants(5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("verified").GetBoolean().ShouldBeFalse();
        json.GetProperty("incompleteCount").GetInt32().ShouldBe(1);
        json.GetProperty("incomplete")[0].GetProperty("id").GetInt32().ShouldBe(11);
        json.GetProperty("incomplete")[0].GetProperty("state").GetString().ShouldBe("Doing");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No children — verified true, zero checked
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyDescendants_NoChildren_ReturnsVerifiedWithZeroChecked()
    {
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().VerifyDescendants(1);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("rootId").GetInt32().ShouldBe(1);
        json.GetProperty("verified").GetBoolean().ShouldBeTrue();
        json.GetProperty("totalChecked").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes workspace key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyDescendants_Response_IncludesWorkspaceKey()
    {
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().VerifyDescendants(1);

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid workspace returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyDescendants_InvalidWorkspace_ReturnsError()
    {
        var result = await CreateSut().VerifyDescendants(1, workspace: "bad/workspace");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom maxDepth is forwarded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyDescendants_CustomMaxDepth_IsRespected()
    {
        var child = new WorkItemBuilder(11, "Child").AsTask().InState("Done").WithParent(5).Build();
        _adoService.FetchChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        var result = await CreateSut().VerifyDescendants(5, maxDepth: 1);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("rootId").GetInt32().ShouldBe(5);
        json.GetProperty("verified").GetBoolean().ShouldBeTrue();

        // maxDepth=1 means only direct children — should NOT fetch grandchildren
        await _adoService.DidNotReceive().FetchChildrenAsync(11, Arg.Any<CancellationToken>());
    }
}
