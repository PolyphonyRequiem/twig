using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.Sprint"/> (twig_sprint MCP tool).
/// Covers iteration path retrieval, optional items listing, empty sprint, and error paths.
/// </summary>
public sealed class NavigationToolsSprintTests : NavigationToolsTestBase
{
    private static IterationPath TestIteration => IterationPath.Parse(@"Twig\Sprint 1").Value;

    // ═══════════════════════════════════════════════════════════════
    //  Default (items = false) — returns iteration path only
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sprint_DefaultNoItems_ReturnsIterationPathWithNullItems()
    {
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(TestIteration);

        var result = await CreateSut().Sprint();

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("iterationPath").GetString().ShouldBe(@"Twig\Sprint 1");
        json.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Null);

        await _workItemRepo.DidNotReceive()
            .GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  items = true — fetches sprint items from cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sprint_WithItems_ReturnsSprintItemsAndCount()
    {
        var item1 = new WorkItemBuilder(1, "Sprint Task A").AsTask().InState("Doing").Build();
        var item2 = new WorkItemBuilder(2, "Sprint Task B").AsTask().InState("To Do").Build();

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(TestIteration);
        _workItemRepo.GetByIterationAsync(TestIteration, Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2 });

        var result = await CreateSut().Sprint(items: true);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("iterationPath").GetString().ShouldBe(@"Twig\Sprint 1");
        json.GetProperty("items").GetArrayLength().ShouldBe(2);
        json.GetProperty("count").GetInt32().ShouldBe(2);
        json.GetProperty("items")[0].GetProperty("id").GetInt32().ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  items = true, sprint is empty
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sprint_WithItemsEmptySprint_ReturnsEmptyArrayAndZeroCount()
    {
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(TestIteration);
        _workItemRepo.GetByIterationAsync(TestIteration, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Sprint(items: true);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("items").GetArrayLength().ShouldBe(0);
        json.GetProperty("count").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Iteration service failure → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sprint_IterationServiceFails_ReturnsError()
    {
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unavailable"));

        var result = await CreateSut().Sprint();

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Failed to get current iteration");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes workspace key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sprint_Response_IncludesWorkspaceKey()
    {
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(TestIteration);

        var result = await CreateSut().Sprint();

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  OperationCanceledException propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sprint_Cancelled_PropagatesException()
    {
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Sprint());
    }
}
