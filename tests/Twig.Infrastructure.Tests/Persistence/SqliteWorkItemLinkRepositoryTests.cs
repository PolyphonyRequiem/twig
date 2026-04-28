using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqliteWorkItemLinkRepository"/>.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqliteWorkItemLinkRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemLinkRepository _repo;

    public SqliteWorkItemLinkRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemLinkRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetLinksAsync_NoLinks_ReturnsEmpty()
    {
        var result = await _repo.GetLinksAsync(999);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAndGetLinks_RoundTrip()
    {
        var links = new List<WorkItemLink>
        {
            new(100, 200, LinkTypes.Related),
            new(100, 300, LinkTypes.Predecessor),
            new(100, 400, LinkTypes.Successor),
        };

        await _repo.SaveLinksAsync(100, links);
        var loaded = await _repo.GetLinksAsync(100);

        loaded.Count.ShouldBe(3);
        loaded.ShouldContain(l => l.SourceId == 100 && l.TargetId == 200 && l.LinkType == LinkTypes.Related);
        loaded.ShouldContain(l => l.SourceId == 100 && l.TargetId == 300 && l.LinkType == LinkTypes.Predecessor);
        loaded.ShouldContain(l => l.SourceId == 100 && l.TargetId == 400 && l.LinkType == LinkTypes.Successor);
    }

    [Fact]
    public async Task SaveLinksAsync_ReplacesExistingLinks()
    {
        var original = new List<WorkItemLink>
        {
            new(100, 200, LinkTypes.Related),
            new(100, 300, LinkTypes.Predecessor),
        };

        await _repo.SaveLinksAsync(100, original);

        var replacement = new List<WorkItemLink>
        {
            new(100, 500, LinkTypes.Successor),
        };

        await _repo.SaveLinksAsync(100, replacement);
        var loaded = await _repo.GetLinksAsync(100);

        loaded.Count.ShouldBe(1);
        loaded[0].TargetId.ShouldBe(500);
        loaded[0].LinkType.ShouldBe(LinkTypes.Successor);
    }

    [Fact]
    public async Task SaveLinksAsync_EmptyList_ClearsExistingLinks()
    {
        var links = new List<WorkItemLink>
        {
            new(100, 200, LinkTypes.Related),
        };

        await _repo.SaveLinksAsync(100, links);
        await _repo.SaveLinksAsync(100, new List<WorkItemLink>());

        var loaded = await _repo.GetLinksAsync(100);
        loaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveLinksAsync_MultipleSources_DoNotInterfere()
    {
        var linksA = new List<WorkItemLink>
        {
            new(100, 200, LinkTypes.Related),
        };
        var linksB = new List<WorkItemLink>
        {
            new(101, 300, LinkTypes.Predecessor),
            new(101, 400, LinkTypes.Successor),
        };

        await _repo.SaveLinksAsync(100, linksA);
        await _repo.SaveLinksAsync(101, linksB);

        var loadedA = await _repo.GetLinksAsync(100);
        var loadedB = await _repo.GetLinksAsync(101);

        loadedA.Count.ShouldBe(1);
        loadedA[0].TargetId.ShouldBe(200);

        loadedB.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SaveLinksAsync_ReplaceOneSource_DoesNotAffectOther()
    {
        await _repo.SaveLinksAsync(100, new List<WorkItemLink>
        {
            new(100, 200, LinkTypes.Related),
        });
        await _repo.SaveLinksAsync(101, new List<WorkItemLink>
        {
            new(101, 300, LinkTypes.Predecessor),
        });

        // Replace source 100's links
        await _repo.SaveLinksAsync(100, new List<WorkItemLink>
        {
            new(100, 500, LinkTypes.Successor),
        });

        var loadedA = await _repo.GetLinksAsync(100);
        var loadedB = await _repo.GetLinksAsync(101);

        loadedA.Count.ShouldBe(1);
        loadedA[0].TargetId.ShouldBe(500);

        loadedB.Count.ShouldBe(1);
        loadedB[0].TargetId.ShouldBe(300);
    }

    [Theory]
    [InlineData(LinkTypes.Related)]
    [InlineData(LinkTypes.Predecessor)]
    [InlineData(LinkTypes.Successor)]
    public async Task SaveAndGetLinks_PreservesLinkType(string linkType)
    {
        var links = new List<WorkItemLink>
        {
            new(100, 200, linkType),
        };

        await _repo.SaveLinksAsync(100, links);
        var loaded = await _repo.GetLinksAsync(100);

        loaded.Count.ShouldBe(1);
        loaded[0].SourceId.ShouldBe(100);
        loaded[0].TargetId.ShouldBe(200);
        loaded[0].LinkType.ShouldBe(linkType);
    }

    [Fact]
    public async Task SaveAndGetLinks_AllThreeMetadataFieldsPreserved()
    {
        var links = new List<WorkItemLink>
        {
            new(42, 100, LinkTypes.Related),
            new(42, 200, LinkTypes.Predecessor),
            new(42, 300, LinkTypes.Successor),
        };

        await _repo.SaveLinksAsync(42, links);
        var loaded = await _repo.GetLinksAsync(42);

        loaded.Count.ShouldBe(3);
        foreach (var link in loaded)
        {
            link.SourceId.ShouldBe(42);
        }
        loaded.ShouldContain(l => l.TargetId == 100 && l.LinkType == LinkTypes.Related);
        loaded.ShouldContain(l => l.TargetId == 200 && l.LinkType == LinkTypes.Predecessor);
        loaded.ShouldContain(l => l.TargetId == 300 && l.LinkType == LinkTypes.Successor);
    }
}
