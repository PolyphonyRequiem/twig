using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.Discard"/> (twig_discard MCP tool).
/// Covers no context, explicit ID, active-item fallback, no pending changes,
/// successful discard, ADO fetch failure, and workspace resolution failure.
/// </summary>
public sealed class MutationToolsDiscardTests : MutationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  No context — no active item, no id supplied
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit ID — uses the supplied id, bypasses context store
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_ExplicitId_UsesProvidedIdDirectly()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((0, 0));

        var result = await CreateMutationSut().Discard(id: 42);

        result.IsError.ShouldBeNull();
        await _contextStore.DidNotReceive().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active-item fallback — no id; resolves from context store
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_NoId_FallsBackToActiveItem()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        var item = new WorkItemBuilder(99, "Active Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(99, Arg.Any<CancellationToken>())
            .Returns((0, 0));

        var result = await CreateMutationSut().Discard();

        result.IsError.ShouldBeNull();
        await _workItemRepo.Received(1).GetByIdAsync(99, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No pending changes — returns discarded=false with message
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_NoPendingChanges_ReturnsDiscardedFalse()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((0, 0));

        var result = await CreateMutationSut().Discard(id: 42);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("discarded").GetBoolean().ShouldBeFalse();
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("message").GetString()!.ShouldContain("#42");

        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Successful discard — clears changes and returns counts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_HasPendingChanges_ClearsAndReturnsDiscardedTrue()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((2, 3));

        var result = await CreateMutationSut().Discard(id: 42);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("discarded").GetBoolean().ShouldBeTrue();
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("notesDiscarded").GetInt32().ShouldBe(2);
        root.GetProperty("fieldEditsDiscarded").GetInt32().ShouldBe(3);

        await _pendingChangeStore.Received(1).ClearChangesAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).ClearDirtyFlagAsync(42, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(5, 7)]
    public async Task Discard_VariousChangeCounts_ReturnsCorrectCounts(int notes, int fieldEdits)
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns((notes, fieldEdits));

        var result = await CreateMutationSut().Discard(id: 42);

        var root = ParseResult(result);
        root.GetProperty("notesDiscarded").GetInt32().ShouldBe(notes);
        root.GetProperty("fieldEditsDiscarded").GetInt32().ShouldBe(fieldEdits);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO fetch failure — cache miss followed by ADO error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_CacheMissAndAdoFails_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unreachable"));

        var result = await CreateMutationSut().Discard(id: 999);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("#999");
        GetErrorText(result).ShouldContain("ADO unreachable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace resolution failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discard_InvalidWorkspace_ReturnsError()
    {
        var result = await CreateMutationSut().Discard(id: 42, workspace: "nonexistent/workspace");

        result.IsError.ShouldBe(true);
    }
}
