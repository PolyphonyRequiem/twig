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

    [Theory]
    [InlineData(false, "scope")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "  ")]
    public async Task WithoutOptInAndScope_MirrorSavesAreNotProjected(bool optedIn, string? scope)
    {
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), optedIn, scope);

        mirror.ProjectsDurableIdentities.ShouldBeFalse();
        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        (await mirror.GetByIdAsync(42)).ShouldNotBeNull();
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task OptedInWithScope_MirrorSavesAreProjected()
    {
        var mirror = SqliteWorkItemRepository.CreateMirror(_store, new WorkItemMapper(), neutralStoreOptedIn: true, "scope");

        mirror.ProjectsDurableIdentities.ShouldBeTrue();
        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        (await new SqliteLocalWorkItemRepository(_store).FindAsync(new ExternalWorkItemIdentity("ado", "scope", "42"))).ShouldNotBeNull();
    }
}
