using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqliteSeedLinkRepository"/>.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqliteSeedLinkRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteSeedLinkRepository _repo;

    public SqliteSeedLinkRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteSeedLinkRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetLinksForItemAsync_NoLinks_ReturnsEmpty()
    {
        var result = await _repo.GetLinksForItemAsync(999);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllSeedLinksAsync_NoLinks_ReturnsEmpty()
    {
        var result = await _repo.GetAllSeedLinksAsync();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAndGetLink_RoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var link = new SeedLink(-1, -2, SeedLinkTypes.Related, now);

        await _repo.AddLinkAsync(link);
        var loaded = await _repo.GetLinksForItemAsync(-1);

        loaded.Count.ShouldBe(1);
        loaded[0].SourceId.ShouldBe(-1);
        loaded[0].TargetId.ShouldBe(-2);
        loaded[0].LinkType.ShouldBe(SeedLinkTypes.Related);
        // DateTimeOffset round-trip via ISO 8601 string
        loaded[0].CreatedAt.ShouldBe(now, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetLinksForItemAsync_ReturnsLinks_WhenItemIsSource()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Blocks, now));

        var links = await _repo.GetLinksForItemAsync(-1);
        links.Count.ShouldBe(1);
        links[0].SourceId.ShouldBe(-1);
    }

    [Fact]
    public async Task GetLinksForItemAsync_ReturnsLinks_WhenItemIsTarget()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Blocks, now));

        var links = await _repo.GetLinksForItemAsync(-2);
        links.Count.ShouldBe(1);
        links[0].TargetId.ShouldBe(-2);
    }

    [Fact]
    public async Task GetLinksForItemAsync_ReturnsAll_WhenItemIsBothSourceAndTarget()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Blocks, now));
        await _repo.AddLinkAsync(new SeedLink(-3, -1, SeedLinkTypes.DependsOn, now));

        var links = await _repo.GetLinksForItemAsync(-1);
        links.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAllSeedLinksAsync_ReturnsAll()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));
        await _repo.AddLinkAsync(new SeedLink(-3, -4, SeedLinkTypes.Blocks, now));
        await _repo.AddLinkAsync(new SeedLink(-5, 100, SeedLinkTypes.DependsOn, now));

        var all = await _repo.GetAllSeedLinksAsync();
        all.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RemoveLinkAsync_RemovesSpecificLink()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));
        await _repo.AddLinkAsync(new SeedLink(-1, -3, SeedLinkTypes.Blocks, now));

        await _repo.RemoveLinkAsync(-1, -2, SeedLinkTypes.Related);

        var links = await _repo.GetLinksForItemAsync(-1);
        links.Count.ShouldBe(1);
        links[0].TargetId.ShouldBe(-3);
        links[0].LinkType.ShouldBe(SeedLinkTypes.Blocks);
    }

    [Fact]
    public async Task RemoveLinkAsync_NoOp_WhenLinkDoesNotExist()
    {
        // Should not throw
        await _repo.RemoveLinkAsync(999, 888, SeedLinkTypes.Related);
        var all = await _repo.GetAllSeedLinksAsync();
        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteLinksForItemAsync_RemovesAll_WhereItemIsSource()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));
        await _repo.AddLinkAsync(new SeedLink(-1, -3, SeedLinkTypes.Blocks, now));
        await _repo.AddLinkAsync(new SeedLink(-4, -5, SeedLinkTypes.DependsOn, now));

        await _repo.DeleteLinksForItemAsync(-1);

        var linksFor1 = await _repo.GetLinksForItemAsync(-1);
        linksFor1.ShouldBeEmpty();

        // Other links should remain
        var all = await _repo.GetAllSeedLinksAsync();
        all.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteLinksForItemAsync_RemovesAll_WhereItemIsTarget()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));
        await _repo.AddLinkAsync(new SeedLink(-3, -2, SeedLinkTypes.Blocks, now));
        await _repo.AddLinkAsync(new SeedLink(-4, -5, SeedLinkTypes.DependsOn, now));

        await _repo.DeleteLinksForItemAsync(-2);

        var linksFor2 = await _repo.GetLinksForItemAsync(-2);
        linksFor2.ShouldBeEmpty();

        var all = await _repo.GetAllSeedLinksAsync();
        all.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteLinksForItemAsync_NoOp_WhenNoLinksExist()
    {
        await _repo.DeleteLinksForItemAsync(999);
        var all = await _repo.GetAllSeedLinksAsync();
        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddLinkAsync_DuplicateKey_Replaces()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMinutes(5);

        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, t1));
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, t2));

        var links = await _repo.GetLinksForItemAsync(-1);
        links.Count.ShouldBe(1);
        links[0].CreatedAt.ShouldBe(t2, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AddLinkAsync_SeedToAdoItem_Works()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, 12345, SeedLinkTypes.Blocks, now));

        var links = await _repo.GetLinksForItemAsync(-1);
        links.Count.ShouldBe(1);
        links[0].TargetId.ShouldBe(12345);
    }
}
