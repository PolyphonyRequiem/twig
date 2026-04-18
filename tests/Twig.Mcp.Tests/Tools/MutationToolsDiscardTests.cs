using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.Discard"/> (twig_discard MCP tool).
/// </summary>
public sealed class MutationToolsDiscardTests : MutationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  No context, no ID — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_NoContextNoId_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unreachable — item in context but not in cache or ADO
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_ActiveItemUnreachable_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(55);
        _workItemRepo.GetByIdAsync(55, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(55, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("#55");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit ID not found in cache — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_ExplicitIdNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var result = await CreateMutationSut().Discard(id: 999);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("#999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No pending changes — returns early with message
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_NoPendingChanges_ReturnsEarlyMessage()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((0, 0));

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("discarded").GetBoolean().ShouldBe(false);
        root.GetProperty("message").GetString()!.ShouldContain("No pending changes");

        // Should NOT have called ClearChangesAsync
        await _pendingChangeStore.DidNotReceive()
            .ClearChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Has pending changes — clears and returns counts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_WithPendingChanges_ClearsAndReturnsCounts()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((2, 3));

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("discarded").GetBoolean().ShouldBe(true);
        root.GetProperty("notesDiscarded").GetInt32().ShouldBe(2);
        root.GetProperty("fieldEditsDiscarded").GetInt32().ShouldBe(3);
        root.GetProperty("id").GetInt32().ShouldBe(42);

        await _pendingChangeStore.Received(1)
            .ClearChangesAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1)
            .ClearDirtyFlagAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit ID — resolves by ID, not active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_ExplicitId_ResolvesById()
    {
        var item = new WorkItemBuilder(77, "Other Task").AsTask().InState("New").Build();
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(77, Arg.Any<CancellationToken>())
            .Returns((1, 0));

        var result = await CreateMutationSut().Discard(id: 77);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(77);
        root.GetProperty("discarded").GetBoolean().ShouldBe(true);
        root.GetProperty("notesDiscarded").GetInt32().ShouldBe(1);

        // Should NOT have consulted active item context
        await _contextStore.DidNotReceive()
            .GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer called on success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_Success_UpdatesPromptState()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((1, 1));

        await CreateMutationSut().Discard();

        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer failure is non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_PromptStateWriterFails_StillSucceeds()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((1, 0));
        _promptStateWriter.WritePromptStateAsync()
            .ThrowsAsync(new InvalidOperationException("Write failed"));

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("discarded").GetBoolean().ShouldBe(true);
    }
}
