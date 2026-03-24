using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqlitePublishIdMapRepository"/>.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqlitePublishIdMapRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePublishIdMapRepository _repo;

    public SqlitePublishIdMapRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqlitePublishIdMapRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task RecordAndGetMapping_RoundTrip()
    {
        await _repo.RecordMappingAsync(-1, 100);

        var newId = await _repo.GetNewIdAsync(-1);

        newId.ShouldBe(100);
    }

    [Fact]
    public async Task GetNewIdAsync_ReturnsNull_WhenNotFound()
    {
        var newId = await _repo.GetNewIdAsync(-999);

        newId.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllMappingsAsync_ReturnsEmpty_WhenNoMappings()
    {
        var mappings = await _repo.GetAllMappingsAsync();

        mappings.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllMappingsAsync_ReturnsAll()
    {
        await _repo.RecordMappingAsync(-1, 100);
        await _repo.RecordMappingAsync(-2, 200);
        await _repo.RecordMappingAsync(-3, 300);

        var mappings = await _repo.GetAllMappingsAsync();

        mappings.Count.ShouldBe(3);
        mappings.ShouldContain(m => m.OldId == -1 && m.NewId == 100);
        mappings.ShouldContain(m => m.OldId == -2 && m.NewId == 200);
        mappings.ShouldContain(m => m.OldId == -3 && m.NewId == 300);
    }

    [Fact]
    public async Task RecordMappingAsync_Replaces_WhenDuplicate()
    {
        await _repo.RecordMappingAsync(-1, 100);
        await _repo.RecordMappingAsync(-1, 200);

        var newId = await _repo.GetNewIdAsync(-1);

        newId.ShouldBe(200);
    }

    [Fact]
    public async Task GetAllMappingsAsync_OrderedByOldId()
    {
        await _repo.RecordMappingAsync(-3, 300);
        await _repo.RecordMappingAsync(-1, 100);
        await _repo.RecordMappingAsync(-2, 200);

        var mappings = await _repo.GetAllMappingsAsync();

        mappings[0].OldId.ShouldBe(-3);
        mappings[1].OldId.ShouldBe(-2);
        mappings[2].OldId.ShouldBe(-1);
    }
}
