using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class SyncCoordinatorFactoryTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();

    private ProtectedCacheWriter CreateProtectedWriter() =>
        new(_workItemRepo, _pendingStore);

    private SyncCoordinatorFactory CreateFactory(int readOnlyMinutes, int readWriteMinutes) =>
        new(_workItemRepo, _adoService, CreateProtectedWriter(), _pendingStore, _linkRepo,
            readOnlyMinutes, readWriteMinutes);

    [Fact]
    public async Task ReadOnly_UsesLongerTtl_ItemFreshForReadOnlyButStaleForReadWrite()
    {
        // Item synced 10 min ago: fresh for RO (15 min) but stale for RW (5 min)
        var factory = CreateFactory(readOnlyMinutes: 15, readWriteMinutes: 5);
        var item = new WorkItemBuilder(42, "Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-10))
            .Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var roResult = await factory.ReadOnly.SyncItemAsync(42);
        roResult.ShouldBeOfType<SyncResult.UpToDate>();

        var rwResult = await factory.ReadWrite.SyncItemAsync(42);
        rwResult.ShouldNotBeOfType<SyncResult.UpToDate>();
    }

    [Fact]
    public async Task Constructor_WhenReadOnlyLessThanReadWrite_ClampsReadOnlyToReadWrite()
    {
        // RO=3 < RW=10 → factory clamps RO to 10
        // Item synced 5 min ago: would be stale at TTL=3 but fresh at TTL=10
        var factory = CreateFactory(readOnlyMinutes: 3, readWriteMinutes: 10);
        var item = new WorkItemBuilder(42, "Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-5))
            .Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        // After clamping, RO TTL = 10, so 5-min-old item is still fresh
        var roResult = await factory.ReadOnly.SyncItemAsync(42);
        roResult.ShouldBeOfType<SyncResult.UpToDate>();
    }

    [Fact]
    public async Task Constructor_WhenEqualValues_BothTiersBehaveIdentically()
    {
        var factory = CreateFactory(readOnlyMinutes: 10, readWriteMinutes: 10);
        var item = new WorkItemBuilder(42, "Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-5))
            .Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var roResult = await factory.ReadOnly.SyncItemAsync(42);
        var rwResult = await factory.ReadWrite.SyncItemAsync(42);

        roResult.ShouldBeOfType<SyncResult.UpToDate>();
        rwResult.ShouldBeOfType<SyncResult.UpToDate>();
    }

    [Fact]
    public async Task Constructor_WithZeroValues_AlwaysStale()
    {
        var factory = CreateFactory(readOnlyMinutes: 0, readWriteMinutes: 0);
        var item = new WorkItemBuilder(42, "Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddSeconds(-1))
            .Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var fetched = new WorkItemBuilder(42, "Item").InState("Active").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(fetched);
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var roResult = await factory.ReadOnly.SyncItemAsync(42);
        roResult.ShouldNotBeOfType<SyncResult.UpToDate>();
    }

    [Fact]
    public void Constructor_WithNullLinkRepo_DoesNotThrow()
    {
        var factory = new SyncCoordinatorFactory(
            _workItemRepo, _adoService, CreateProtectedWriter(), _pendingStore,
            null, readOnlyStaleMinutes: 15, readWriteStaleMinutes: 5);

        factory.ReadOnly.ShouldNotBeNull();
        factory.ReadWrite.ShouldNotBeNull();
    }
}
