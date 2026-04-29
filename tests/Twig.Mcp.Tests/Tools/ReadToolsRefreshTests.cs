using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Sync;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ReadTools.Refresh"/> (twig_refresh MCP tool).
/// Covers full context refresh, single-item refresh, no-context, ADO failure,
/// workspace not found, and regression: pending changes are never pushed.
/// </summary>
public sealed class ReadToolsRefreshTests : ReadToolsTestBase
{
    private readonly TwigConfiguration _config = new()
    {
        Display = new DisplayConfig { CacheStaleMinutes = 5 },
    };

    // ═══════════════════════════════════════════════════════════════
    //  Full context refresh — no id, active item exists
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_NoId_RefreshesActiveContext()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .LastSyncedAt(null).Build();
        var child = new WorkItemBuilder(43, "Child").AsTask().InState("To Do")
            .WithParent(42).LastSyncedAt(null).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(43, Arg.Any<CancellationToken>()).Returns(child);

        var stats = new CacheStatistics(TrackedItemCount: 2, NewestSyncUtc: DateTimeOffset.UtcNow, OldestSyncUtc: DateTimeOffset.UtcNow);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        var result = await sut.Refresh();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("refreshedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        data.GetProperty("lastSyncUtc").GetString().ShouldNotBeNullOrEmpty();
        data.GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item refresh — id provided
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_WithId_RefreshesOnlySingleItem()
    {
        var item = new WorkItemBuilder(99, "Target Item").AsTask().InState("Doing")
            .LastSyncedAt(null).Build();

        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(item);

        var stats = new CacheStatistics(TrackedItemCount: 1, NewestSyncUtc: DateTimeOffset.UtcNow, OldestSyncUtc: DateTimeOffset.UtcNow);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        var result = await sut.Refresh(id: 99);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("refreshedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        data.GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);

        // Should have called FetchAsync for the specified item
        await _adoService.Received().FetchAsync(99, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item, no id — returns zero refreshed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_NoActiveItem_NoId_ReturnsZeroRefreshed()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var stats = new CacheStatistics(TrackedItemCount: 0, NewestSyncUtc: null, OldestSyncUtc: null);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        var result = await sut.Refresh();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("refreshedCount").GetInt32().ShouldBe(0);
        data.GetProperty("lastSyncUtc").GetString().ShouldBe("");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO failure during full context refresh — best effort
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_AdoFailure_BestEffort_StillReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .LastSyncedAt(null).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var stats = new CacheStatistics(TrackedItemCount: 1, NewestSyncUtc: null, OldestSyncUtc: null);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        var result = await sut.Refresh();

        // Should still succeed — sync failure is best-effort
        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("refreshedCount").GetInt32().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace not found — returns error envelope
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_WorkspaceNotFound_ReturnsError()
    {
        var sut = CreateSut(_config);
        var result = await sut.Refresh(workspace: "unknown/workspace");

        result.IsError.ShouldBe(true);
        var text = GetErrorText(result);
        text.ShouldContain("unknown/workspace");
    }

    // ═══════════════════════════════════════════════════════════════
    //  REGRESSION: Pending changes are never pushed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_NeverPushesPendingChanges()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var stats = new CacheStatistics(TrackedItemCount: 0, NewestSyncUtc: null, OldestSyncUtc: null);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        await sut.Refresh();

        // The flusher should never be invoked — refresh is pull-only
        await _pendingChangeStore.DidNotReceive()
            .GetDirtyItemIdsAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Envelope shape — success envelope has context block
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_SuccessEnvelope_HasContextBlock()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var stats = new CacheStatistics(TrackedItemCount: 0, NewestSyncUtc: null, OldestSyncUtc: null);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        var result = await sut.Refresh();

        var envelope = ParseEnvelope(result);
        envelope.GetProperty("success").GetBoolean().ShouldBeTrue();
        envelope.TryGetProperty("data", out _).ShouldBeTrue();
        envelope.TryGetProperty("context", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response shape — all required fields present
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_ResponseHasExpectedShape()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var stats = new CacheStatistics(TrackedItemCount: 0, NewestSyncUtc: null, OldestSyncUtc: null);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        var result = await sut.Refresh();

        var data = ParseResult(result);
        data.TryGetProperty("refreshedCount", out _).ShouldBeTrue();
        data.TryGetProperty("lastSyncUtc", out _).ShouldBeTrue();
        data.TryGetProperty("durationMs", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item refresh does NOT touch active context
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_WithId_DoesNotResolveActiveItem()
    {
        var item = new WorkItemBuilder(55, "Specific Item").AsTask().InState("To Do")
            .LastSyncedAt(null).Build();

        _workItemRepo.GetByIdAsync(55, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(55, Arg.Any<CancellationToken>()).Returns(item);

        var stats = new CacheStatistics(TrackedItemCount: 1, NewestSyncUtc: DateTimeOffset.UtcNow, OldestSyncUtc: DateTimeOffset.UtcNow);
        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);

        var sut = CreateSut(_config);
        await sut.Refresh(id: 55);

        // Parent chain and children should NOT be fetched when id is provided
        await _workItemRepo.DidNotReceive()
            .GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive()
            .GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
