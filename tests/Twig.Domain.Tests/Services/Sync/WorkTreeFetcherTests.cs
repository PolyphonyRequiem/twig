using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Sync;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

/// <summary>
/// Unit tests for <see cref="WorkTreeFetcher.FetchDescendantsAsync"/> delegate-based overload.
/// Covers recursive traversal, depth limiting, cancellation, and empty parent handling.
/// </summary>
public sealed class WorkTreeFetcherTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Basic recursive traversal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchDescendantsAsync_SingleLevel_PopulatesResultForParent()
    {
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().Build();
        var child1 = new WorkItemBuilder(10, "Child A").AsFeature().WithParent(1).Build();
        var child2 = new WorkItemBuilder(11, "Child B").AsFeature().WithParent(1).Build();

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkItem>>(id == 1
                ? new[] { child1, child2 }
                : Array.Empty<WorkItem>());

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [parent], remainingDepth: 1, result);

        result.ShouldContainKey(1);
        result[1].Count.ShouldBe(2);
        result[1][0].Id.ShouldBe(10);
        result[1][1].Id.ShouldBe(11);
    }

    [Fact]
    public async Task FetchDescendantsAsync_MultiLevel_RecursesThroughChildren()
    {
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().Build();
        var child = new WorkItemBuilder(10, "Child").AsFeature().WithParent(1).Build();
        var grandchild = new WorkItemBuilder(100, "Grandchild").AsTask().WithParent(10).Build();

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
        {
            return id switch
            {
                1 => Task.FromResult<IReadOnlyList<WorkItem>>(new[] { child }),
                10 => Task.FromResult<IReadOnlyList<WorkItem>>(new[] { grandchild }),
                _ => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>())
            };
        }

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [parent], remainingDepth: 2, result);

        result.ShouldContainKey(1);
        result.ShouldContainKey(10);
        result[1][0].Id.ShouldBe(10);
        result[10][0].Id.ShouldBe(100);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Depth limiting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchDescendantsAsync_DepthZero_DoesNotFetchAnyChildren()
    {
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().Build();
        var callCount = 0;

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());
        }

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [parent], remainingDepth: 0, result);

        callCount.ShouldBe(0);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchDescendantsAsync_DepthOne_DoesNotRecurseIntoChildren()
    {
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().Build();
        var child = new WorkItemBuilder(10, "Child").AsFeature().WithParent(1).Build();

        var fetchedIds = new List<int>();

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
        {
            fetchedIds.Add(id);
            return id == 1
                ? Task.FromResult<IReadOnlyList<WorkItem>>(new[] { child })
                : Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());
        }

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [parent], remainingDepth: 1, result);

        // Only parent should be fetched — child not recursed into
        fetchedIds.ShouldBe([1]);
        result.ShouldContainKey(1);
        result.ShouldNotContainKey(10);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty parents list
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchDescendantsAsync_EmptyParents_DoesNothing()
    {
        var callCount = 0;

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());
        }

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [], remainingDepth: 5, result);

        callCount.ShouldBe(0);
        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Node with no children — not added to result
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchDescendantsAsync_NodeWithNoChildren_NotAddedToResult()
    {
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().Build();

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [parent], remainingDepth: 3, result);

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchDescendantsAsync_DelegateCancelled_PropagatesException()
    {
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().Build();

        Task<IReadOnlyList<WorkItem>> FetchChildren(int id, CancellationToken ct)
            => Task.FromException<IReadOnlyList<WorkItem>>(new OperationCanceledException());

        var result = new Dictionary<int, IReadOnlyList<WorkItem>>();

        await Should.ThrowAsync<OperationCanceledException>(
            () => WorkTreeFetcher.FetchDescendantsAsync(FetchChildren, [parent], remainingDepth: 1, result));
    }
}
