using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Cli.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IStagedIdentityRegistry"/> for tests. Mints sequential
/// negative aliases (-1, -2, -3, ...) paired with fresh staged identities.
/// </summary>
public sealed class FakeStagedIdentityRegistry : IStagedIdentityRegistry
{
    private readonly Dictionary<StagedIdentity, StagedAlias> _byIdentity = new();
    private readonly Dictionary<StagedAlias, StagedIdentity> _byAlias = new();
    private int _floor;

    public Task<StagedSeedIdentity> MintAsync(CancellationToken ct = default)
    {
        var alias = StagedAlias.Below(_floor);
        _floor = alias.Value;
        var identity = StagedIdentity.New();
        _byIdentity[identity] = alias;
        _byAlias[alias] = identity;
        return Task.FromResult(new StagedSeedIdentity(identity, alias));
    }

    public Task RetireAsync(StagedIdentity identity, CancellationToken ct = default)
    {
        if (_byIdentity.Remove(identity, out var alias))
        {
            _byAlias.Remove(alias);
        }

        return Task.CompletedTask;
    }

    public Task<StagedIdentity?> FindByAliasAsync(StagedAlias alias, CancellationToken ct = default) =>
        Task.FromResult(_byAlias.TryGetValue(alias, out var identity) ? identity : (StagedIdentity?)null);

    public Task<StagedAlias?> FindAliasAsync(StagedIdentity identity, CancellationToken ct = default) =>
        Task.FromResult(_byIdentity.TryGetValue(identity, out var alias) ? alias : (StagedAlias?)null);
}
