using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for GetByIterationAndAssigneeAsync in SqliteWorkItemRepository.
/// </summary>
public class SqliteWorkItemRepositoryAssigneeTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public SqliteWorkItemRepositoryAssigneeTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetByIterationAndAssigneeAsync_ReturnsOnlyMatchingItems()
    {
        var alice = CreateWorkItem(1, "Task A", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");
        var bob = CreateWorkItem(2, "Task B", "Active", assignedTo: "Bob Jones", iterationPath: @"Project\Sprint1");
        var aliceSprint2 = CreateWorkItem(3, "Task C", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint2");

        await _repo.SaveAsync(alice);
        await _repo.SaveAsync(bob);
        await _repo.SaveAsync(aliceSprint2);

        var iterPath = IterationPath.Parse(@"Project\Sprint1");
        var results = await _repo.GetByIterationAndAssigneeAsync(iterPath.Value, "Alice Smith");

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
        results[0].AssignedTo.ShouldBe("Alice Smith");
    }

    [Fact]
    public async Task GetByIterationAndAssigneeAsync_CaseInsensitive()
    {
        var item = CreateWorkItem(1, "Task A", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");
        await _repo.SaveAsync(item);

        var iterPath = IterationPath.Parse(@"Project\Sprint1");
        var results = await _repo.GetByIterationAndAssigneeAsync(iterPath.Value, "alice smith");

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetByIterationAndAssigneeAsync_ReturnsEmpty_WhenNoMatch()
    {
        var item = CreateWorkItem(1, "Task A", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");
        await _repo.SaveAsync(item);

        var iterPath = IterationPath.Parse(@"Project\Sprint1");
        var results = await _repo.GetByIterationAndAssigneeAsync(iterPath.Value, "Nobody");

        results.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetByIterationAndAssigneeAsync_ExcludesUnassigned()
    {
        var assigned = CreateWorkItem(1, "Assigned", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");
        var unassigned = CreateWorkItem(2, "Unassigned", "Active", assignedTo: null, iterationPath: @"Project\Sprint1");

        await _repo.SaveAsync(assigned);
        await _repo.SaveAsync(unassigned);

        var iterPath = IterationPath.Parse(@"Project\Sprint1");
        var results = await _repo.GetByIterationAndAssigneeAsync(iterPath.Value, "Alice Smith");

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetByIterationAndAssigneeAsync_MultipleMatchesReturned()
    {
        var item1 = CreateWorkItem(1, "Task 1", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");
        var item2 = CreateWorkItem(2, "Task 2", "New", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");
        var item3 = CreateWorkItem(3, "Task 3", "Active", assignedTo: "Alice Smith", iterationPath: @"Project\Sprint1");

        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);
        await _repo.SaveAsync(item3);

        var iterPath = IterationPath.Parse(@"Project\Sprint1");
        var results = await _repo.GetByIterationAndAssigneeAsync(iterPath.Value, "Alice Smith");

        results.Count.ShouldBe(3);
    }

    private static WorkItem CreateWorkItem(
        int id, string title, string state,
        string? assignedTo = null, string? iterationPath = null)
    {
        var typeResult = WorkItemType.Parse("Task");
        var iterResult = IterationPath.Parse(iterationPath ?? @"Project\Sprint1");
        var areaResult = AreaPath.Parse(@"Project\Area");

        return new WorkItem
        {
            Id = id,
            Type = typeResult.Value,
            Title = title,
            State = state,
            AssignedTo = assignedTo,
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
        };
    }
}
