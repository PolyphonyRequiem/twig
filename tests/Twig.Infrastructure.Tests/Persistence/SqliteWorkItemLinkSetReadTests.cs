using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqliteWorkItemLinkRepository.GetLinksForSetAsync"/> — the
/// plural read added by ADO #154 so a set-reading consumer can ask for the edges belonging to
/// a whole set in one query.
/// </summary>
/// <remarks>
/// 🔴 Fixture design note: the store is seeded with FOUR sources and every test requests a
/// PROPER SUBSET of them. That is load-bearing — a mutant that ignores the id filter and
/// returns the whole table would pass against a fixture where the requested set is everything.
/// Each assertion therefore checks both that the requested sources are present AND that the
/// unrequested ones are absent.
/// </remarks>
public class SqliteWorkItemLinkSetReadTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemLinkRepository _repo;

    public SqliteWorkItemLinkSetReadTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemLinkRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    private async Task SeedAsync()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);
        await _repo.SaveLinksAsync(101, [new WorkItemLink(101, 201, LinkTypes.Successor)]);
        // 102 deliberately has NO links — a member of the set with no edges.
        await _repo.SaveLinksAsync(102, []);
        // 999 is never requested by any test — the control that catches an unfiltered read.
        await _repo.SaveLinksAsync(999, [new WorkItemLink(999, 888, LinkTypes.Related)]);
    }

    [Fact]
    public async Task GetLinksForSetAsync_ReturnsEdgesForEveryRequestedId()
    {
        await SeedAsync();

        var result = await _repo.GetLinksForSetAsync([100, 101]);

        // Both requested sources contribute — the whole point of the plural read.
        result.ShouldContain(l => l.SourceId == 100 && l.TargetId == 200 && l.LinkType == LinkTypes.Predecessor);
        result.ShouldContain(l => l.SourceId == 101 && l.TargetId == 201 && l.LinkType == LinkTypes.Successor);
        // Exact count, not just containment: a wider read would add rows without removing these.
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetLinksForSetAsync_DoesNotReturnEdgesForUnrequestedIds()
    {
        await SeedAsync();

        var result = await _repo.GetLinksForSetAsync([100]);

        result.ShouldNotContain(l => l.SourceId == 999);
        result.ShouldNotContain(l => l.SourceId == 101);
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetLinksForSetAsync_MemberWithNoEdges_ContributesNothingAndDoesNotFail()
    {
        await SeedAsync();

        var result = await _repo.GetLinksForSetAsync([100, 102]);

        result.Count.ShouldBe(1);
        result[0].SourceId.ShouldBe(100);
    }

    [Fact]
    public async Task GetLinksForSetAsync_DuplicateIds_DoNotDuplicateRows()
    {
        await SeedAsync();

        var result = await _repo.GetLinksForSetAsync([100, 100, 100]);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetLinksForSetAsync_EmptyInput_ReturnsEmpty()
    {
        await SeedAsync();

        var result = await _repo.GetLinksForSetAsync([]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLinksForSetAsync_UnknownIds_ReturnEmptyRatherThanEverything()
    {
        await SeedAsync();

        var result = await _repo.GetLinksForSetAsync([500, 501]);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// The plural read must agree with the singular one id-for-id, so callers can migrate
    /// without a behaviour change. Uses a source whose edge set has more than one member so
    /// a "returns only the first row" mutant cannot pass.
    /// </summary>
    [Fact]
    public async Task GetLinksForSetAsync_AgreesWithSingularReadForTheSameId()
    {
        await _repo.SaveLinksAsync(100,
        [
            new WorkItemLink(100, 200, LinkTypes.Related),
            new WorkItemLink(100, 300, LinkTypes.Predecessor),
            new WorkItemLink(100, 400, LinkTypes.Successor),
        ]);

        var singular = await _repo.GetLinksAsync(100);
        var plural = await _repo.GetLinksForSetAsync([100]);

        // Precondition: the fixture has to be plural or the comparison proves nothing.
        singular.Count.ShouldBe(3);
        plural.OrderBy(l => l.TargetId).ShouldBe(singular.OrderBy(l => l.TargetId));
    }

    /// <summary>
    /// An id whose value contains SQL metacharacters cannot arrive through the int-typed
    /// parameter, but the placeholder names are built into the command text, so this pins
    /// that a large set still binds one parameter per distinct id and returns correctly.
    /// </summary>
    [Fact]
    public async Task GetLinksForSetAsync_LargeSet_BindsEveryDistinctId()
    {
        var ids = new List<int>();
        for (var i = 1; i <= 250; i++)
        {
            ids.Add(i);
            await _repo.SaveLinksAsync(i, [new WorkItemLink(i, i + 10_000, LinkTypes.Related)]);
        }

        var result = await _repo.GetLinksForSetAsync(ids);

        result.Count.ShouldBe(250);
        result.Select(l => l.SourceId).Distinct().Count().ShouldBe(250);
    }
}
