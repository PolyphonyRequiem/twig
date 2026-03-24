using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests verifying that the publish pipeline's transactional
/// operations use the ambient <see cref="SqliteCacheStore.ActiveTransaction"/>
/// correctly. All tests use a real in-memory SQLite database to catch
/// transaction enrollment bugs that mocked tests cannot detect.
/// </summary>
public class SeedPublishTransactionIntegrationTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _workItemRepo;
    private readonly SqliteSeedLinkRepository _seedLinkRepo;
    private readonly SqlitePublishIdMapRepository _publishIdMapRepo;
    private readonly SqliteUnitOfWork _unitOfWork;

    public SeedPublishTransactionIntegrationTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _workItemRepo = new SqliteWorkItemRepository(_store);
        _seedLinkRepo = new SqliteSeedLinkRepository(_store);
        _publishIdMapRepo = new SqlitePublishIdMapRepository(_store);
        _unitOfWork = new SqliteUnitOfWork(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task CommittedTransaction_PersistsAllOperations()
    {
        // Arrange: create seed and child seed with a seed link
        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Seed",
            State = "New", ParentId = 100, IsSeed = true,
        };
        var childSeed = new WorkItem
        {
            Id = -2, Type = WorkItemType.Task, Title = "Child",
            State = "New", ParentId = -1, IsSeed = true,
        };
        await _workItemRepo.SaveAsync(seed);
        await _workItemRepo.SaveAsync(childSeed);
        await _seedLinkRepo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow));

        var fetchedItem = new WorkItem
        {
            Id = 500, Type = WorkItemType.Task, Title = "Seed",
            State = "New", ParentId = 100, IsSeed = true,
        };

        // Act: run the full transactional publish flow
        var tx = await _unitOfWork.BeginAsync();
        try
        {
            await _publishIdMapRepo.RecordMappingAsync(-1, 500);
            await _seedLinkRepo.RemapIdAsync(-1, 500);
            await _workItemRepo.RemapParentIdAsync(-1, 500);
            await _workItemRepo.DeleteByIdAsync(-1);
            await _workItemRepo.SaveAsync(fetchedItem);
            await _unitOfWork.CommitAsync(tx);
        }
        catch
        {
            await _unitOfWork.RollbackAsync(tx);
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }

        // Assert: all changes persisted
        var mapping = await _publishIdMapRepo.GetNewIdAsync(-1);
        mapping.ShouldBe(500);

        var oldSeed = await _workItemRepo.GetByIdAsync(-1);
        oldSeed.ShouldBeNull();

        var newItem = await _workItemRepo.GetByIdAsync(500);
        newItem.ShouldNotBeNull();
        newItem!.Title.ShouldBe("Seed");

        var child = await _workItemRepo.GetByIdAsync(-2);
        child!.ParentId.ShouldBe(500);

        var links = await _seedLinkRepo.GetLinksForItemAsync(500);
        links.Count.ShouldBe(1);
        links[0].SourceId.ShouldBe(500);
    }

    [Fact]
    public async Task RolledBackTransaction_LeavesDbUnchanged()
    {
        // Arrange: create a seed
        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Seed",
            State = "New", IsSeed = true,
        };
        await _workItemRepo.SaveAsync(seed);
        await _seedLinkRepo.AddLinkAsync(new SeedLink(-1, -3, SeedLinkTypes.Related, DateTimeOffset.UtcNow));

        // Act: start transaction, do partial work, then rollback
        var tx = await _unitOfWork.BeginAsync();
        await _publishIdMapRepo.RecordMappingAsync(-1, 500);
        await _seedLinkRepo.RemapIdAsync(-1, 500);
        await _workItemRepo.DeleteByIdAsync(-1);
        await _unitOfWork.RollbackAsync(tx);
        await tx.DisposeAsync();

        // Assert: database is unchanged — all operations rolled back
        var mapping = await _publishIdMapRepo.GetNewIdAsync(-1);
        mapping.ShouldBeNull();

        var originalSeed = await _workItemRepo.GetByIdAsync(-1);
        originalSeed.ShouldNotBeNull();
        originalSeed!.Title.ShouldBe("Seed");

        var links = await _seedLinkRepo.GetLinksForItemAsync(-1);
        links.Count.ShouldBe(1);
        links[0].SourceId.ShouldBe(-1);
    }

    [Fact]
    public async Task FailureMidTransaction_RollbackLeavesDbUnchanged()
    {
        // Arrange: create seed with child
        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Seed",
            State = "New", IsSeed = true,
        };
        var child = new WorkItem
        {
            Id = -2, Type = WorkItemType.Task, Title = "Child",
            State = "New", ParentId = -1, IsSeed = true,
        };
        await _workItemRepo.SaveAsync(seed);
        await _workItemRepo.SaveAsync(child);

        // Act: start transaction, do some work, then simulate failure + rollback
        var tx = await _unitOfWork.BeginAsync();
        try
        {
            await _publishIdMapRepo.RecordMappingAsync(-1, 500);
            await _workItemRepo.RemapParentIdAsync(-1, 500);
            // Simulate failure before delete+save
            throw new InvalidOperationException("Simulated failure");
        }
        catch (InvalidOperationException)
        {
            await _unitOfWork.RollbackAsync(tx);
        }
        finally
        {
            await tx.DisposeAsync();
        }

        // Assert: all changes rolled back
        var mapping = await _publishIdMapRepo.GetNewIdAsync(-1);
        mapping.ShouldBeNull();

        var childItem = await _workItemRepo.GetByIdAsync(-2);
        childItem!.ParentId.ShouldBe(-1); // Not remapped
    }
}
