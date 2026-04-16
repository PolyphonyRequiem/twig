using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.Sync"/> (twig_sync MCP tool).
/// </summary>
public sealed class MutationToolsSyncTests : MutationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  No pending changes — flushed zero
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_NoPendingChanges_ReturnsFlushedZero()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Sync();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("flushed").GetInt32().ShouldBe(0);
        root.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes — flushes them
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_PendingChanges_FlushesThem()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 42 });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(42, "note", null, null, "A pending note")
            });

        // AddCommentAsync succeeds for the pending note
        // FetchAsync for post-flush resync
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // No active context → skip pull phase
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Sync();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("flushed").GetInt32().ShouldBe(1);
        root.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item — skips pull phase
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_NoActiveItem_SkipsPullPhase()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Sync();

        result.IsError.ShouldBeNull();

        // FetchAsync should not have been called for pull sync (no context item)
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active item — syncs item and children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_ActiveItem_SyncsItemAndChildren()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .LastSyncedAt(null).Build();
        var child = new WorkItemBuilder(43, "Child Task").AsTask().InState("To Do")
            .WithParent(42).LastSyncedAt(null).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // FetchAsync will be called for sync — item has no LastSyncedAt so it's stale
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(43, Arg.Any<CancellationToken>()).Returns(child);

        // Provide pending change store empty for sync guard checks
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateMutationSut().Sync();

        result.IsError.ShouldBeNull();

        // Should have called FetchAsync at least for the active item
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Sync fails — best effort (still returns success)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_SyncFails_BestEffort()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .LastSyncedAt(null).Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // SyncItemSetAsync internally calls FetchAsync which fails
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await CreateMutationSut().Sync();

        // Should still return success — sync failure is best-effort
        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("flushed").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer called
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_PromptStateWriterCalled()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        await CreateMutationSut().Sync();

        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response contains flush summary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_ResponseContainsFlushSummary()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Sync();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.TryGetProperty("flushed", out _).ShouldBe(true);
        root.TryGetProperty("failed", out _).ShouldBe(true);
        root.TryGetProperty("failures", out var failures).ShouldBe(true);
        failures.GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Flush failure — still returns response (not error)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_FlushFailure_StillReturnsResponse()
    {
        // Set up a dirty item that fails to flush
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 99 });
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null); // Item not in cache → failure entry

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Sync();

        // Tool returns success (not error) even when flush has failures
        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("flushed").GetInt32().ShouldBe(0);
        root.GetProperty("failed").GetInt32().ShouldBe(1);
        var failures = root.GetProperty("failures");
        failures.GetArrayLength().ShouldBe(1);
        failures[0].GetProperty("workItemId").GetInt32().ShouldBe(99);
    }
}
