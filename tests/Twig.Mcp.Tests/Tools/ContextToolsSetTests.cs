using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ContextTools.Set"/> (twig_set MCP tool).
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

        // Workspace field — validates FormatWorkItemWithWorkingSet emits workspace key
        root.GetProperty("workspace").GetString().ShouldBe("testorg/testproject");
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

}
