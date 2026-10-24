using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Epic #1168: the durable identity projection is off by default, and even when opted in it is
/// refused without a known ADO resource scope.
/// </summary>
public sealed class NeutralStoreOptInTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");

    public void Dispose() => _store.Dispose();

    private long CountIdentities()
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending.external_identities";
        return (long)cmd.ExecuteScalar()!;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("1", true)]
    public void OnlyTheExactValueOne_EnablesTheSwitch(string? value, bool expected)
        => NeutralStoreOptIn.IsEnabled(value).ShouldBe(expected);

    private static Func<CancellationToken, Task<string?>> Returns(string? scope)
        => _ => Task.FromResult(scope);

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task WithoutOptInOrScopeSource_MirrorSavesAreNotProjected(bool optedIn, bool hasSource)
    {
        var calls = 0;
        Func<CancellationToken, Task<string?>>? source = hasSource
            ? _ => { calls++; return Task.FromResult<string?>("scope"); }
            : null;
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), optedIn, source);

        mirror.ProjectsDurableIdentities.ShouldBeFalse();
        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        (await mirror.GetByIdAsync(42)).ShouldNotBeNull();
        CountIdentities().ShouldBe(0);
        calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ScopeResolvingNullOrBlank_RefusesProjection_ButStillWritesTheMirror(string? scope)
    {
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true, Returns(scope));

        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        await mirror.SaveBatchAsync([new WorkItemBuilder(43, "Other").Build()]);

        (await mirror.GetByIdAsync(42)).ShouldNotBeNull();
        (await mirror.GetByIdAsync(43)).ShouldNotBeNull();
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task OptedInWithScope_MirrorSavesAreProjected()
    {
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true, Returns("scope"));

        mirror.ProjectsDurableIdentities.ShouldBeTrue();
        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        (await new SqliteLocalWorkItemRepository(_store).FindAsync(new ExternalWorkItemIdentity("ado", "scope", "42"))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Scope_IsResolvedLazily_OnceAcrossSaves()
    {
        var calls = 0;
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true,
            _ => { calls++; return Task.FromResult<string?>("scope"); });

        calls.ShouldBe(0);
        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        await mirror.SaveBatchAsync([new WorkItemBuilder(43, "Other").Build()]);

        calls.ShouldBe(1);
        CountIdentities().ShouldBe(2);
    }

    [Fact]
    public async Task NullScope_IsAlsoResolvedOnce_SoRefusalCostsNoRepeatedRoundTrips()
    {
        var calls = 0;
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true,
            _ => { calls++; return Task.FromResult<string?>(null); });

        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        await mirror.SaveAsync(new WorkItemBuilder(43, "Other").Build());

        calls.ShouldBe(1);
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task ResolutionFailure_Surfaces_WritesNothing_AndIsRetried()
    {
        var calls = 0;
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true,
            _ => ++calls == 1
                ? Task.FromException<string?>(new HttpRequestException("transport"))
                : Task.FromResult<string?>("scope"));

        await Should.ThrowAsync<HttpRequestException>(() => mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build()));
        (await mirror.GetByIdAsync(42)).ShouldBeNull();
        _store.ActiveTransaction.ShouldBeNull();

        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        calls.ShouldBe(2);
        CountIdentities().ShouldBe(1);
    }

    [Fact]
    public async Task InvalidResolvedScope_IsRejected_NotNormalized()
    {
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true, Returns(" padded"));

        await Should.ThrowAsync<ArgumentException>(() => mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build()));
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task RefusedScope_SingleSave_StillJoinsTheAmbientTransaction()
    {
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true, Returns(null));
        var conn = _store.GetConnection();
        using (var tx = conn.BeginTransaction())
        {
            _store.ActiveTransaction = tx;
            try
            {
                await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
                tx.Rollback();
            }
            finally { _store.ActiveTransaction = null; }
        }

        (await mirror.GetByIdAsync(42)).ShouldBeNull();
    }
}
