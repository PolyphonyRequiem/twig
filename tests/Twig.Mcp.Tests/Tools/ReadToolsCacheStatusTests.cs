using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ReadTools.CacheStatus"/> (twig_cache_status MCP tool).
/// Covers happy path, empty cache, pending changes, workspace not found, and oldest item age.
/// </summary>
public sealed class ReadToolsCacheStatusTests : ReadToolsTestBase
{
    private readonly TwigConfiguration _config = new()
    {
        Display = new DisplayConfig { CacheStaleMinutes = 5 },
    };

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — cache with items and no pending changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_WithItems_ReturnsCorrectStatistics()
    {
        var now = DateTimeOffset.UtcNow;
        var stats = new CacheStatistics(
            TrackedItemCount: 42,
            NewestSyncUtc: now.AddMinutes(-1),
            OldestSyncUtc: now.AddMinutes(-30));

        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);
        _pendingChangeStore.GetTotalPendingChangeCountAsync(Arg.Any<CancellationToken>()).Returns(0);

        var sut = CreateSut(_config);
        var result = await sut.CacheStatus();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("lastSyncUtc").GetString().ShouldNotBeNullOrEmpty();
        data.GetProperty("pendingChangeCount").GetInt32().ShouldBe(0);
        data.GetProperty("trackedItemCount").GetInt32().ShouldBe(42);
        data.GetProperty("oldestItemAgeSeconds").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty cache — no items at all
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_EmptyCache_ReturnsZeroCounts()
    {
        var stats = new CacheStatistics(
            TrackedItemCount: 0,
            NewestSyncUtc: null,
            OldestSyncUtc: null);

        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);
        _pendingChangeStore.GetTotalPendingChangeCountAsync(Arg.Any<CancellationToken>()).Returns(0);

        var sut = CreateSut(_config);
        var result = await sut.CacheStatus();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("lastSyncUtc").GetString().ShouldBe("");
        data.GetProperty("pendingChangeCount").GetInt32().ShouldBe(0);
        data.GetProperty("trackedItemCount").GetInt32().ShouldBe(0);
        data.GetProperty("oldestItemAgeSeconds").GetInt64().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  With pending changes — non-zero count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_WithPendingChanges_ReturnsPendingCount()
    {
        var now = DateTimeOffset.UtcNow;
        var stats = new CacheStatistics(
            TrackedItemCount: 5,
            NewestSyncUtc: now,
            OldestSyncUtc: now.AddHours(-2));

        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);
        _pendingChangeStore.GetTotalPendingChangeCountAsync(Arg.Any<CancellationToken>()).Returns(7);

        var sut = CreateSut(_config);
        var result = await sut.CacheStatus();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("pendingChangeCount").GetInt32().ShouldBe(7);
        data.GetProperty("trackedItemCount").GetInt32().ShouldBe(5);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Oldest item age — verifies seconds calculation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_OldestItemAge_IsPositiveSeconds()
    {
        var now = DateTimeOffset.UtcNow;
        var oldestSync = now.AddMinutes(-60);
        var stats = new CacheStatistics(
            TrackedItemCount: 10,
            NewestSyncUtc: now.AddMinutes(-1),
            OldestSyncUtc: oldestSync);

        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);
        _pendingChangeStore.GetTotalPendingChangeCountAsync(Arg.Any<CancellationToken>()).Returns(0);

        var sut = CreateSut(_config);
        var result = await sut.CacheStatus();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        // Should be roughly 3600 seconds (60 minutes), allow some tolerance
        var ageSeconds = data.GetProperty("oldestItemAgeSeconds").GetInt64();
        ageSeconds.ShouldBeGreaterThanOrEqualTo(3590);
        ageSeconds.ShouldBeLessThanOrEqualTo(3610);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace not found — returns error envelope
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_WorkspaceNotFound_ReturnsError()
    {
        var sut = CreateSut(_config);
        var result = await sut.CacheStatus(workspace: "unknown/workspace");

        result.IsError.ShouldBe(true);
        var text = GetErrorText(result);
        text.ShouldContain("unknown/workspace");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Envelope shape — success envelope has context block
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_SuccessEnvelope_HasContextBlock()
    {
        var stats = new CacheStatistics(
            TrackedItemCount: 1,
            NewestSyncUtc: DateTimeOffset.UtcNow,
            OldestSyncUtc: DateTimeOffset.UtcNow);

        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);
        _pendingChangeStore.GetTotalPendingChangeCountAsync(Arg.Any<CancellationToken>()).Returns(0);

        var sut = CreateSut(_config);
        var result = await sut.CacheStatus();

        var envelope = ParseEnvelope(result);
        envelope.GetProperty("success").GetBoolean().ShouldBeTrue();
        envelope.TryGetProperty("data", out _).ShouldBeTrue();
        envelope.TryGetProperty("context", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No network calls — only repo and pending store are accessed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CacheStatus_DoesNotCallAdoService()
    {
        var stats = new CacheStatistics(
            TrackedItemCount: 3,
            NewestSyncUtc: DateTimeOffset.UtcNow,
            OldestSyncUtc: DateTimeOffset.UtcNow);

        _workItemRepo.GetCacheStatisticsAsync(Arg.Any<CancellationToken>()).Returns(stats);
        _pendingChangeStore.GetTotalPendingChangeCountAsync(Arg.Any<CancellationToken>()).Returns(0);

        var sut = CreateSut(_config);
        await sut.CacheStatus();

        // Verify no ADO network calls were made
        await _adoService.DidNotReceiveWithAnyArgs().FetchAsync(default, default);
        await _adoService.DidNotReceiveWithAnyArgs().FetchChildrenAsync(default, default);
    }
}
