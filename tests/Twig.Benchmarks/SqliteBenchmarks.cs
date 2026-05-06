using BenchmarkDotNet.Attributes;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;

namespace Twig.Benchmarks;

/// <summary>
/// Benchmarks for SQLite work item repository operations.
/// Uses an in-memory database seeded with varying item counts.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class SqliteBenchmarks
{
    private SqliteCacheStore _store = null!;
    private SqliteWorkItemRepository _repo = null!;
    private int _testItemId;

    [Params(50, 500, 5000)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        var mapper = new WorkItemMapper();
        _repo = new SqliteWorkItemRepository(_store, mapper);

        // Seed the database with work items
        var items = new List<WorkItem>(ItemCount);
        for (var i = 1; i <= ItemCount; i++)
        {
            var snapshot = new WorkItemSnapshot
            {
                Id = i,
                Title = $"Work Item {i}",
                State = "Active",
                TypeName = "Task",
                AssignedTo = "Test User",
                AreaPath = "Project\\Team",
                IterationPath = "Project\\Sprint 1",
                ParentId = i > 10 ? (i % 10) + 1 : null, // First 10 are roots, rest are children
                Revision = 1,
            };
            items.Add(mapper.Map(snapshot));
        }

        _repo.SaveBatchAsync(items).GetAwaiter().GetResult();
        _testItemId = ItemCount / 2; // Pick a middle item for single lookups
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
    }

    [Benchmark(Description = "GetById (single item)")]
    public async Task<WorkItem?> GetById()
    {
        return await _repo.GetByIdAsync(_testItemId);
    }

    [Benchmark(Description = "GetChildren (of root item)")]
    public async Task<IReadOnlyList<WorkItem>> GetChildren()
    {
        return await _repo.GetChildrenAsync(1);
    }

    [Benchmark(Description = "GetByIds (10 items)")]
    public async Task<IReadOnlyList<WorkItem>> GetByIds()
    {
        var ids = Enumerable.Range(1, 10);
        return await _repo.GetByIdsAsync(ids);
    }

    [Benchmark(Description = "GetParentChain")]
    public async Task<IReadOnlyList<WorkItem>> GetParentChain()
    {
        return await _repo.GetParentChainAsync(_testItemId);
    }

    [Benchmark(Description = "SaveAsync (single item)")]
    public async Task SaveSingle()
    {
        var item = (await _repo.GetByIdAsync(_testItemId))!;
        await _repo.SaveAsync(item);
    }

    [Benchmark(Description = "FindByPattern")]
    public async Task<IReadOnlyList<WorkItem>> FindByPattern()
    {
        return await _repo.FindByPatternAsync("Work Item 2");
    }
}
