using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ActiveItemResolverTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly ActiveItemResolver _sut;

    public ActiveItemResolverTests()
    {
        _sut = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetActiveItemAsync — cache hit
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetActiveItemAsync_CacheHit_ReturnsFound()
    {
        var item = MakeItem(42);
        _contextStore.GetActiveWorkItemIdAsync().Returns(42);
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var result = await _sut.GetActiveItemAsync();

        result.ShouldBeOfType<ActiveItemResult.Found>()
              .WorkItem.Id.ShouldBe(42);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetActiveItemAsync — cache miss → auto-fetch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetActiveItemAsync_CacheMiss_FetchesAndReturnsFetchedFromAdo()
    {
        var fetched = MakeItem(42);
        _contextStore.GetActiveWorkItemIdAsync().Returns(42);
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);
        _adoService.FetchAsync(42).Returns(fetched);

        var result = await _sut.GetActiveItemAsync();

        result.ShouldBeOfType<ActiveItemResult.FetchedFromAdo>()
              .WorkItem.Id.ShouldBe(42);
        await _workItemRepo.Received(1).SaveAsync(fetched, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetActiveItemAsync — fetch fails → Unreachable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetActiveItemAsync_FetchFails_ReturnsUnreachable()
    {
        _contextStore.GetActiveWorkItemIdAsync().Returns(42);
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);
        _adoService.FetchAsync(42).Throws(new HttpRequestException("Network error"));

        var result = await _sut.GetActiveItemAsync();

        var unreachable = result.ShouldBeOfType<ActiveItemResult.Unreachable>();
        unreachable.Id.ShouldBe(42);
        unreachable.Reason.ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetActiveItemAsync — no active ID → NoContext
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetActiveItemAsync_NoActiveId_ReturnsNoContext()
    {
        _contextStore.GetActiveWorkItemIdAsync().Returns((int?)null);

        var result = await _sut.GetActiveItemAsync();

        result.ShouldBeOfType<ActiveItemResult.NoContext>();
        await _workItemRepo.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByIdAsync — cache hit
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveByIdAsync_CacheHit_ReturnsFound()
    {
        var item = MakeItem(99);
        _workItemRepo.GetByIdAsync(99).Returns(item);

        var result = await _sut.ResolveByIdAsync(99);

        result.ShouldBeOfType<ActiveItemResult.Found>()
              .WorkItem.Id.ShouldBe(99);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByIdAsync — cache miss → auto-fetch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveByIdAsync_CacheMiss_FetchesAndReturnsFetchedFromAdo()
    {
        var fetched = MakeItem(99);
        _workItemRepo.GetByIdAsync(99).Returns((WorkItem?)null);
        _adoService.FetchAsync(99).Returns(fetched);

        var result = await _sut.ResolveByIdAsync(99);

        result.ShouldBeOfType<ActiveItemResult.FetchedFromAdo>()
              .WorkItem.Id.ShouldBe(99);
        await _workItemRepo.Received(1).SaveAsync(fetched, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByIdAsync — fetch fails → Unreachable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveByIdAsync_FetchFails_ReturnsUnreachable()
    {
        _workItemRepo.GetByIdAsync(99).Returns((WorkItem?)null);
        _adoService.FetchAsync(99).Throws(new HttpRequestException("Timeout"));

        var result = await _sut.ResolveByIdAsync(99);

        var unreachable = result.ShouldBeOfType<ActiveItemResult.Unreachable>();
        unreachable.Id.ShouldBe(99);
        unreachable.Reason.ShouldContain("Timeout");
    }

    // ═══════════════════════════════════════════════════════════════
    //  CancellationToken forwarding
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetActiveItemAsync_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _contextStore.GetActiveWorkItemIdAsync(cts.Token).Returns(42);
        _workItemRepo.GetByIdAsync(42, cts.Token).Returns(MakeItem(42));

        await _sut.GetActiveItemAsync(cts.Token);

        await _contextStore.Received(1).GetActiveWorkItemIdAsync(cts.Token);
        await _workItemRepo.Received(1).GetByIdAsync(42, cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation propagation — OperationCanceledException is NOT swallowed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveByIdAsync_CancellationDuringFetch_PropagatesException()
    {
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.ResolveByIdAsync(42));
    }

    [Fact]
    public async Task GetActiveItemAsync_CancellationDuringFetch_PropagatesException()
    {
        _contextStore.GetActiveWorkItemIdAsync().Returns(42);
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.GetActiveItemAsync());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeItem(int id) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = $"Item {id}",
        State = "Active",
    };
}
