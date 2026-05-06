using BenchmarkDotNet.Attributes;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;

namespace Twig.Benchmarks;

/// <summary>
/// Benchmarks for command-level operations that exercise the full
/// cache read → format path (minus actual ADO calls and Spectre rendering).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CommandBenchmarks
{
    private SqliteCacheStore _store = null!;
    private SqliteWorkItemRepository _repo = null!;
    private SqliteContextStore _contextStore = null!;

    [GlobalSetup]
    public void Setup()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        var mapper = new WorkItemMapper();
        _repo = new SqliteWorkItemRepository(_store, mapper);
        _contextStore = new SqliteContextStore(_store);

        // Seed 200 items in a tree structure
        var items = new List<WorkItem>(200);
        for (var i = 1; i <= 200; i++)
        {
            var snapshot = new WorkItemSnapshot
            {
                Id = i,
                Title = $"Work Item {i}",
                State = i % 3 == 0 ? "Done" : "Active",
                TypeName = i <= 10 ? "Epic" : (i <= 50 ? "Issue" : "Task"),
                AssignedTo = "Test User",
                AreaPath = "Project\\Team",
                IterationPath = "Project\\Sprint 1",
                ParentId = i <= 10 ? null : (i <= 50 ? ((i - 11) % 10) + 1 : ((i - 51) % 40) + 11),
                Revision = 1,
            };
            items.Add(mapper.Map(snapshot));
        }

        _repo.SaveBatchAsync(items).GetAwaiter().GetResult();
        _contextStore.SetActiveWorkItemIdAsync(50).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
    }

    [Benchmark(Description = "Show: load item + children + parent")]
    public async Task ShowCommandPath()
    {
        var item = await _repo.GetByIdAsync(50);
        if (item is null) return;
        var children = await _repo.GetChildrenAsync(item.Id);
        WorkItem? parent = item.ParentId.HasValue
            ? await _repo.GetByIdAsync(item.ParentId.Value)
            : null;
    }

    [Benchmark(Description = "Tree: load item + parent chain + children")]
    public async Task TreeCommandPath()
    {
        var item = await _repo.GetByIdAsync(50);
        if (item is null) return;
        var parentChain = await _repo.GetParentChainAsync(item.Id);
        var children = await _repo.GetChildrenAsync(item.Id);
    }

    [Benchmark(Description = "Query: find by pattern")]
    public async Task QueryCommandPath()
    {
        var results = await _repo.FindByPatternAsync("Work Item");
    }
}
