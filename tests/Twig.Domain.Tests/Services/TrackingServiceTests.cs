using NSubstitute;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public sealed class TrackingServiceTests
{
    private readonly ITrackingRepository _repository = Substitute.For<ITrackingRepository>();

    private TrackingService CreateSut() => new(_repository);

    // ═══════════════════════════════════════════════════════════════
    //  TrackAsync
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(TrackingMode.Single)]
    [InlineData(TrackingMode.Tree)]
    public async Task TrackAsync_DelegatesToRepository_WithCorrectModeAndId(TrackingMode mode)
    {
        var sut = CreateSut();

        await sut.TrackAsync(42, mode);

        await _repository.Received(1).UpsertTrackedAsync(42, mode, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.TrackAsync(1, TrackingMode.Single, token);

        await _repository.Received(1).UpsertTrackedAsync(1, TrackingMode.Single, token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TrackTreeAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TrackTreeAsync_DelegatesToTrackAsync_WithTreeMode()
    {
        var sut = CreateSut();

        await sut.TrackTreeAsync(99);

        await _repository.Received(1).UpsertTrackedAsync(99, TrackingMode.Tree, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackTreeAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.TrackTreeAsync(7, token);

        await _repository.Received(1).UpsertTrackedAsync(7, TrackingMode.Tree, token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  UntrackAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UntrackAsync_WhenTracked_RemovesAndReturnsTrue()
    {
        var sut = CreateSut();
        _repository.GetTrackedByWorkItemIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new TrackedItem(42, TrackingMode.Single, DateTimeOffset.UtcNow));

        var result = await sut.UntrackAsync(42);

        result.ShouldBeTrue();
        await _repository.Received(1).RemoveTrackedAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UntrackAsync_WhenNotTracked_ReturnsFalseAndSkipsRemove()
    {
        var sut = CreateSut();
        _repository.GetTrackedByWorkItemIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((TrackedItem?)null);

        var result = await sut.UntrackAsync(42);

        result.ShouldBeFalse();
        await _repository.DidNotReceive().RemoveTrackedAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UntrackAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        _repository.GetTrackedByWorkItemIdAsync(42, cts.Token)
            .Returns(new TrackedItem(42, TrackingMode.Single, DateTimeOffset.UtcNow));

        await sut.UntrackAsync(42, cts.Token);

        await _repository.Received(1).RemoveTrackedAsync(42, cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExcludeAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExcludeAsync_DelegatesToRepository()
    {
        var sut = CreateSut();

        await sut.ExcludeAsync(55);

        await _repository.Received(1).AddExcludedAsync(55, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExcludeAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        await sut.ExcludeAsync(55, cts.Token);

        await _repository.Received(1).AddExcludedAsync(55, cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetTrackedItemsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTrackedItemsAsync_ReturnsAllTracked()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(1, TrackingMode.Single, DateTimeOffset.UtcNow),
            new(2, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var result = await sut.GetTrackedItemsAsync();

        result.ShouldBe(items);
    }

    [Fact]
    public async Task GetTrackedItemsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());

        var result = await sut.GetTrackedItemsAsync();

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetExcludedIdsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetExcludedIdsAsync_ReturnsWorkItemIds()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem>
        {
            new(10, "noise", DateTimeOffset.UtcNow),
            new(20, "irrelevant", DateTimeOffset.UtcNow),
            new(30, "done", DateTimeOffset.UtcNow),
        };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.GetExcludedIdsAsync();

        result.ShouldBe(new[] { 10, 20, 30 });
    }

    [Fact]
    public async Task GetExcludedIdsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.GetExcludedIdsAsync();

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ListExclusionsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListExclusionsAsync_ReturnsAllExcludedItems()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem>
        {
            new(10, "noise", DateTimeOffset.UtcNow),
            new(20, "irrelevant", DateTimeOffset.UtcNow),
        };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.ListExclusionsAsync();

        result.ShouldBe(excluded);
    }

    [Fact]
    public async Task ListExclusionsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.ListExclusionsAsync();

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  RemoveExclusionAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RemoveExclusionAsync_WhenExcluded_RemovesAndReturnsTrue()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem> { new(42, "noise", DateTimeOffset.UtcNow) };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.RemoveExclusionAsync(42);

        result.ShouldBeTrue();
        await _repository.Received(1).RemoveExcludedAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveExclusionAsync_WhenNotExcluded_ReturnsFalse()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.RemoveExclusionAsync(42);

        result.ShouldBeFalse();
        await _repository.DidNotReceive().RemoveExcludedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ClearExclusionsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClearExclusionsAsync_WithExclusions_ClearsAndReturnsCount()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem>
        {
            new(10, "noise", DateTimeOffset.UtcNow),
            new(20, "done", DateTimeOffset.UtcNow),
        };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.ClearExclusionsAsync();

        result.ShouldBe(2);
        await _repository.Received(1).ClearAllExcludedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearExclusionsAsync_WhenEmpty_ReturnsZeroAndSkipsClear()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.ClearExclusionsAsync();

        result.ShouldBe(0);
        await _repository.DidNotReceive().ClearAllExcludedAsync(Arg.Any<CancellationToken>());
    }
}
