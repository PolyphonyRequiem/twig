using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// AB#831 tests for the edge-set verification stamp: the thing that lets a caller tell a
/// VERIFIED-empty edge set apart from one that was never fetched.
/// </summary>
/// <remarks>
/// 🔴 Fixture design note: the discriminating case in this file is always a pair — an id that was
/// saved with an EMPTY link list, beside an id that was never saved at all. Both return
/// <c>[]</c> from <see cref="SqliteWorkItemLinkRepository.GetLinksAsync"/>, so a test that only
/// read the edges could not fail no matter what the stamp did. Every assertion here therefore
/// contrasts the two.
/// </remarks>
public class SqliteWorkItemLinkVerificationTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemLinkRepository _repo;

    public SqliteWorkItemLinkVerificationTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemLinkRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    /// <summary>
    /// The headline distinction. Both ids read back with zero edges; only one of them has been
    /// asked about.
    /// </summary>
    [Fact]
    public async Task VerifiedEmptyEdgeSet_IsDistinguishableFromNeverFetched()
    {
        await _repo.SaveLinksAsync(100, []);

        (await _repo.GetLinksAsync(100)).ShouldBeEmpty();
        (await _repo.GetLinksAsync(200)).ShouldBeEmpty();

        // ...and that is the whole bug: identical edge reads, different truths.
        (await _repo.GetLinksVerifiedAtAsync(100)).ShouldNotBeNull();
        (await _repo.GetLinksVerifiedAtAsync(200)).ShouldBeNull();
    }

    [Fact]
    public async Task SaveLinksAsync_WithEdges_StampsTheSource()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);

        var verifiedAt = await _repo.GetLinksVerifiedAtAsync(100);

        verifiedAt.ShouldNotBeNull();
        verifiedAt.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        verifiedAt.Value.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    /// <summary>
    /// The stamp is only ever claimed for the id that was saved. A write that stamped its link
    /// TARGETS as well would report a verified edge set for items nobody fetched — the original
    /// failure mode, rebuilt.
    /// </summary>
    [Fact]
    public async Task SaveLinksAsync_DoesNotStampTheLinkTargets()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);

        (await _repo.GetLinksVerifiedAtAsync(200)).ShouldBeNull();
    }

    /// <summary>
    /// Re-saving moves the stamp forward rather than inserting a second row. Uses an id that
    /// already has a stamp so a plain INSERT would violate the primary key and throw.
    /// </summary>
    [Fact]
    public async Task SaveLinksAsync_Twice_AdvancesTheStampWithoutFailing()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);
        var first = await _repo.GetLinksVerifiedAtAsync(100);
        await Task.Delay(15);

        await _repo.SaveLinksAsync(100, []);
        var second = await _repo.GetLinksVerifiedAtAsync(100);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second.Value.ShouldBeGreaterThan(first.Value);
        // The edge removal landed as well — the stamp is not decoration on a stale edge set.
        (await _repo.GetLinksAsync(100)).ShouldBeEmpty();
    }

    /// <summary>
    /// 🔴 The set read is the point of the plural form: a consumer walking a frontier of N
    /// candidates learns which members it may trust in ONE query, without the per-id refresh the
    /// ticket costed out. Requests a proper superset of the saved ids so an implementation that
    /// ignored the filter and returned the whole table could not pass.
    /// </summary>
    [Fact]
    public async Task GetLinksVerifiedAtForSetAsync_ReportsOnlyTheFetchedMembers()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);
        await _repo.SaveLinksAsync(101, []);
        await _repo.SaveLinksAsync(999, [new WorkItemLink(999, 888, LinkTypes.Related)]);

        var verified = await _repo.GetLinksVerifiedAtForSetAsync([100, 101, 102]);

        verified.ShouldContainKey(100);
        verified.ShouldContainKey(101);
        // 102 was never fetched — ABSENT, not present-with-a-null.
        verified.ShouldNotContainKey(102);
        // 999 was fetched but not requested.
        verified.ShouldNotContainKey(999);
        verified.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetLinksVerifiedAtForSetAsync_AgreesWithTheSingularRead()
    {
        await _repo.SaveLinksAsync(100, []);

        var plural = await _repo.GetLinksVerifiedAtForSetAsync([100]);
        var singular = await _repo.GetLinksVerifiedAtAsync(100);

        singular.ShouldNotBeNull();
        plural[100].ShouldBe(singular.Value);
    }

    [Fact]
    public async Task GetLinksVerifiedAtForSetAsync_DuplicateIds_YieldOneEntry()
    {
        await _repo.SaveLinksAsync(100, []);

        var verified = await _repo.GetLinksVerifiedAtForSetAsync([100, 100, 100]);

        verified.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetLinksVerifiedAtForSetAsync_EmptyInput_ReturnsEmpty()
    {
        await _repo.SaveLinksAsync(100, []);

        var verified = await _repo.GetLinksVerifiedAtForSetAsync([]);

        verified.ShouldBeEmpty();
    }

    /// <summary>
    /// A fresh git worktree is the worst case named on the ticket: its cache is empty, so every
    /// link read returns <c>[]</c>. It must now say so.
    /// </summary>
    [Fact]
    public async Task EmptyCache_ReportsEveryIdAsUnverified()
    {
        var verified = await _repo.GetLinksVerifiedAtForSetAsync([742, 743, 744]);

        verified.ShouldBeEmpty();
        (await _repo.GetLinksVerifiedAtAsync(742)).ShouldBeNull();
    }

    // ── Batch replace (one transaction) ─────────────────────────────

    /// <summary>
    /// The batch form must be indistinguishable in RESULT from N singular calls — it exists only
    /// to collapse N fsyncing commits into one. Includes an edgeless key, because that is the
    /// case whose stamp carries all the meaning.
    /// </summary>
    [Fact]
    public async Task SaveLinksForSourcesAsync_WritesEveryKey_IncludingEdgelessOnes()
    {
        await _repo.SaveLinksForSourcesAsync(new Dictionary<int, IReadOnlyList<WorkItemLink>>
        {
            [100] = [new WorkItemLink(100, 200, LinkTypes.Predecessor)],
            [101] = [],
        });

        (await _repo.GetLinksAsync(100)).Count.ShouldBe(1);
        (await _repo.GetLinksAsync(101)).ShouldBeEmpty();
        (await _repo.GetLinksVerifiedAtAsync(100)).ShouldNotBeNull();
        // The edgeless key is VERIFIED, not merely absent — the whole point.
        (await _repo.GetLinksVerifiedAtAsync(101)).ShouldNotBeNull();
        // A key nobody wrote stays unverified.
        (await _repo.GetLinksVerifiedAtAsync(102)).ShouldBeNull();
    }

    /// <summary>
    /// Replace semantics survive batching: a source's PREVIOUS edges must be gone, not merged.
    /// </summary>
    [Fact]
    public async Task SaveLinksForSourcesAsync_ReplacesRatherThanMerges()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);

        await _repo.SaveLinksForSourcesAsync(new Dictionary<int, IReadOnlyList<WorkItemLink>>
        {
            [100] = [new WorkItemLink(100, 300, LinkTypes.Successor)],
        });

        var links = await _repo.GetLinksAsync(100);
        links.Count.ShouldBe(1);
        links[0].TargetId.ShouldBe(300);
    }

    /// <summary>
    /// All-or-nothing: the batch shares one transaction, so a failure part-way must leave the
    /// store exactly as it was rather than half-updated. Forced with a duplicate edge, which
    /// violates the work_item_links primary key.
    /// </summary>
    [Fact]
    public async Task SaveLinksForSourcesAsync_OnFailure_RollsBackTheWholeBatch()
    {
        await _repo.SaveLinksAsync(100, [new WorkItemLink(100, 200, LinkTypes.Predecessor)]);
        var stampBefore = await _repo.GetLinksVerifiedAtAsync(100);

        var duplicate = new WorkItemLink(101, 999, LinkTypes.Related);
        await Should.ThrowAsync<Exception>(() => _repo.SaveLinksForSourcesAsync(
            new Dictionary<int, IReadOnlyList<WorkItemLink>>
            {
                [100] = [new WorkItemLink(100, 300, LinkTypes.Successor)],
                [101] = [duplicate, duplicate],
            }));

        // #100's earlier state survived intact — neither its edges nor its stamp moved.
        var links = await _repo.GetLinksAsync(100);
        links.Count.ShouldBe(1);
        links[0].TargetId.ShouldBe(200);
        (await _repo.GetLinksVerifiedAtAsync(100)).ShouldBe(stampBefore);
        (await _repo.GetLinksVerifiedAtAsync(101)).ShouldBeNull();
    }

    [Fact]
    public async Task SaveLinksForSourcesAsync_EmptyMap_IsANoOp()
    {
        await _repo.SaveLinksForSourcesAsync(new Dictionary<int, IReadOnlyList<WorkItemLink>>());

        (await _repo.GetLinksVerifiedAtForSetAsync([100, 101])).ShouldBeEmpty();
    }
}
