using Shouldly;
using Twig.Domain.ValueObjects;
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
        _registry = new SqliteStagedIdentityRegistry(_store);
        _repo = new SqlitePublishIdMapRepository(_store, _registry);
    }

    private readonly SqliteStagedIdentityRegistry _registry;

    // Wayfinder 0014: the map is keyed on StagedIdentity, so a fixture mints through the
    // register rather than inventing a bare int. The alias comes back with it, which is what
    // the display-side assertions below use.
    private Task<StagedSeedIdentity> MintAsync() => _registry.MintAsync();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task RecordAndGetMapping_RoundTrip()
    {
        var seed = await MintAsync();

        await _repo.RecordMappingAsync(seed.Identity, 100);

        var newId = await _repo.GetNewIdAsync(seed.Identity);

        newId.ShouldBe(100);
    }

    [Fact]
    public async Task GetNewIdAsync_ReturnsNull_WhenNotFound()
    {
        var unpublished = await MintAsync();

        var newId = await _repo.GetNewIdAsync(unpublished.Identity);

        newId.ShouldBeNull();
    }

    [Fact]
    public async Task GetNewIdByAliasAsync_ResolvesThroughTheRegister()
    {
        // twig history hands us a number the user typed. It is an alias, not a key, so it
        // resolves through the durable register before touching the map.
        var seed = await MintAsync();
        await _repo.RecordMappingAsync(seed.Identity, 100);

        (await _repo.GetNewIdByAliasAsync(seed.Alias)).ShouldBe(100);
    }

    [Fact]
    public async Task GetNewIdByAliasAsync_ReturnsNull_ForAnUnknownAlias_RatherThanCoercingIt()
    {
        // 0003 §4: twig does not coerce an unknown value into a plausible known one. An
        // alias that was never issued must stay visibly unknown, not resolve to a neighbour.
        var seed = await MintAsync();
        await _repo.RecordMappingAsync(seed.Identity, 100);

        StagedAlias.TryFrom(-9999, out var neverIssued).ShouldBeTrue();

        (await _repo.GetNewIdByAliasAsync(neverIssued)).ShouldBeNull();
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
        var a = await MintAsync();
        var b = await MintAsync();
        var c = await MintAsync();

        await _repo.RecordMappingAsync(a.Identity, 100);
        await _repo.RecordMappingAsync(b.Identity, 200);
        await _repo.RecordMappingAsync(c.Identity, 300);

        var mappings = await _repo.GetAllMappingsAsync();

        mappings.Count.ShouldBe(3);
        mappings.ShouldContain(m => m.Identity == a.Identity && m.Alias == a.Alias && m.NewId == 100);
        mappings.ShouldContain(m => m.Identity == b.Identity && m.Alias == b.Alias && m.NewId == 200);
        mappings.ShouldContain(m => m.Identity == c.Identity && m.Alias == c.Alias && m.NewId == 300);
    }

    [Fact]
    public async Task RecordMappingAsync_Replaces_WhenDuplicate()
    {
        var seed = await MintAsync();

        await _repo.RecordMappingAsync(seed.Identity, 100);
        await _repo.RecordMappingAsync(seed.Identity, 200);

        var newId = await _repo.GetNewIdAsync(seed.Identity);

        newId.ShouldBe(200);
        (await _repo.GetAllMappingsAsync()).Count.ShouldBe(1, "re-recording the same identity updates in place");
    }

    [Fact]
    public async Task GetAllMappingsAsync_OrderedByAlias()
    {
        var a = await MintAsync();   // alias -1
        var b = await MintAsync();   // alias -2
        var c = await MintAsync();   // alias -3

        await _repo.RecordMappingAsync(c.Identity, 300);
        await _repo.RecordMappingAsync(a.Identity, 100);
        await _repo.RecordMappingAsync(b.Identity, 200);

        var mappings = await _repo.GetAllMappingsAsync();

        // Ordering is a display concern over the alias — the same order the old OldId
        // ordering produced. Nothing joins on it.
        mappings[0].Alias.ShouldBe(c.Alias);
        mappings[1].Alias.ShouldBe(b.Alias);
        mappings[2].Alias.ShouldBe(a.Alias);
    }

    [Fact]
    public async Task RecordedMapping_SurvivesUnderTheIdentity_WhenTheAliasIsReusedForDisplay()
    {
        // The point of the re-key (#280): the mapping is reachable by something a cache
        // rebuild cannot invalidate, and two distinct seeds never share a key.
        var first = await MintAsync();
        var second = await MintAsync();

        await _repo.RecordMappingAsync(first.Identity, 111);
        await _repo.RecordMappingAsync(second.Identity, 222);

        (await _repo.GetNewIdAsync(first.Identity)).ShouldBe(111);
        (await _repo.GetNewIdAsync(second.Identity)).ShouldBe(222);
        first.Alias.ShouldNotBe(second.Alias);
    }
}
