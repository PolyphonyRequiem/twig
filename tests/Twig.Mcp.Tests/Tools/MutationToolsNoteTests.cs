using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.Note"/> (twig_note MCP tool).
/// </summary>
public sealed class MutationToolsNoteTests : MutationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Validation — empty / whitespace text
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Note_EmptyOrWhitespaceText_ReturnsError(string text)
    {
        var result = await CreateMutationSut().Note(text);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("requires non-empty text");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No context — no active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Note("A comment");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO push succeeds — isPending false
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_AdoPushSucceeds_IsPendingFalse()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // AddCommentAsync succeeds (no throw)
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateMutationSut().Note("This is a comment");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("isPending").GetBoolean().ShouldBe(false);
        root.GetProperty("noteAdded").GetBoolean().ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO unreachable — stages locally, isPending true
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_AdoUnreachable_StagesLocally_IsPendingTrue()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.AddCommentAsync(42, "A note", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateMutationSut().Note("A note");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("isPending").GetBoolean().ShouldBe(true);

        // Should have staged the change locally
        await _pendingChangeStore.Received(1).AddChangeAsync(
            42, "note", Arg.Any<string?>(), Arg.Any<string?>(), "A note",
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO push succeeds — clears staged notes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_AdoPushSucceeds_ClearsStaged()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        await CreateMutationSut().Note("Comment text");

        await _pendingChangeStore.Received(1).ClearChangesByTypeAsync(
            42, "note", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO unreachable — does NOT clear staged
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_AdoUnreachable_DoesNotClearStaged()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.AddCommentAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        await CreateMutationSut().Note("Some note");

        await _pendingChangeStore.DidNotReceive().ClearChangesByTypeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Resync after success — best effort (failure is non-fatal)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_ResyncAfterSuccess_BestEffort()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // AddCommentAsync succeeds, but FetchAsync (resync) fails
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Resync failure"));

        var result = await CreateMutationSut().Note("A comment");

        // Should still succeed even though resync failed
        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("isPending").GetBoolean().ShouldBe(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer called
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_PromptStateWriterCalled()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        await CreateMutationSut().Note("A note");

        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response contains id and title
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_ResponseContainsIdAndTitle()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateMutationSut().Note("Some note");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("My Task");
    }
}
