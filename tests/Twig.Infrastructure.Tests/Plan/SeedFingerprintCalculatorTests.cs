using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Focused behavioural tests for <see cref="SeedFingerprintCalculator"/>. These pin the
/// property that the fingerprint canonicalizes every endpoint (parent + link source/target)
/// on the <b>stable staged identity</b> a peer was minted under, so publishing that peer
/// between plan and apply cannot perturb the drift detector.
/// <para>
/// Each test drives the calculator twice: once with the peer visible only by its negative
/// alias, once with the peer's publish mapping in place (and the neighbour rewired onto the
/// positive ADO id it landed at). The two hashes must be byte-identical. A separate
/// negative pins that a truly-unrelated positive id — one that has never been recorded in
/// the publish map — is <em>not</em> aliased onto any staged identity.
/// </para>
/// </summary>
public sealed class SeedFingerprintCalculatorTests
{
    private static readonly StagedIdentity SeedIdentity =
        StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-0000000000a1"));
    private static readonly StagedIdentity ParentIdentity =
        StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-0000000000a2"));
    private static readonly StagedIdentity LinkPeerIdentity =
        StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-0000000000a3"));

    private static readonly StagedAlias SeedAlias = Alias(-11);
    private static readonly StagedAlias ParentAlias = Alias(-12);
    private static readonly StagedAlias LinkPeerAlias = Alias(-13);

    // ── parent-before / parent-after publish ───────────────────────────────

    [Fact]
    public async Task Fingerprint_is_stable_when_parent_publishes_first()
    {
        // Before: parent visible only by its negative alias.
        var (registryBefore, publishMapBefore) = Registry(
            aliases: new (StagedAlias, StagedIdentity)[]
            {
                (SeedAlias, SeedIdentity), (ParentAlias, ParentIdentity),
            },
            mappings: Array.Empty<PublishMapping>());

        var seedBefore = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: ParentAlias.Value);

        // After: parent has been assigned ADO id 5000 by a prior publish.
        var (registryAfter, publishMapAfter) = Registry(
            aliases: new (StagedAlias, StagedIdentity)[]
            {
                (SeedAlias, SeedIdentity), (ParentAlias, ParentIdentity),
            },
            mappings: new[] { new PublishMapping(ParentIdentity, ParentAlias, 5000) });

        // The cache now records the neighbour by its ADO id; the fingerprint must ignore
        // this and still resolve back to the same identity token.
        var seedAfter = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: 5000);

        var before = await SeedFingerprintCalculator.ComputeAsync(
            seedBefore, Array.Empty<SeedLink>(), registryBefore, publishMapBefore);
        var after = await SeedFingerprintCalculator.ComputeAsync(
            seedAfter, Array.Empty<SeedLink>(), registryAfter, publishMapAfter);

        after.ShouldBe(before);
        before.Length.ShouldBe(64); // lowercase-hex SHA-256
    }

    [Fact]
    public async Task Fingerprint_differs_when_parent_is_an_unrelated_real_ado_id()
    {
        // A registered parent with a publish mapping onto 5000: seed token.
        var (registryMapped, publishMapMapped) = Registry(
            aliases: new (StagedAlias, StagedIdentity)[]
            {
                (SeedAlias, SeedIdentity), (ParentAlias, ParentIdentity),
            },
            mappings: new[] { new PublishMapping(ParentIdentity, ParentAlias, 5000) });

        // Same seed shape, but parented under a real ADO item nobody staged.
        var (registryReal, publishMapReal) = Registry(
            aliases: new[] { (SeedAlias, SeedIdentity) },
            mappings: Array.Empty<PublishMapping>());

        var seedMapped = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: 5000);
        var seedReal = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: 5000);

        var mapped = await SeedFingerprintCalculator.ComputeAsync(
            seedMapped, Array.Empty<SeedLink>(), registryMapped, publishMapMapped);
        var real = await SeedFingerprintCalculator.ComputeAsync(
            seedReal, Array.Empty<SeedLink>(), registryReal, publishMapReal);

        real.ShouldNotBe(mapped);
    }

    // ── link-peer-before / link-peer-after mapping ─────────────────────────

    [Fact]
    public async Task Fingerprint_is_stable_when_linked_peer_publishes_first()
    {
        var link = new SeedLink(SeedAlias.Value, LinkPeerAlias.Value,
            SeedLinkTypes.DependsOn, DateTimeOffset.UnixEpoch);

        // Before: the link still names the negative peer alias on both ends of the cache.
        var (registryBefore, publishMapBefore) = Registry(
            aliases: new (StagedAlias, StagedIdentity)[]
            {
                (SeedAlias, SeedIdentity), (LinkPeerAlias, LinkPeerIdentity),
            },
            mappings: Array.Empty<PublishMapping>());
        var seedBefore = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: null);
        var linksBefore = new[] { link };

        // After: the peer published as ADO 7000, and the local cache rewrote the link
        // endpoint onto that positive id.
        var (registryAfter, publishMapAfter) = Registry(
            aliases: new (StagedAlias, StagedIdentity)[]
            {
                (SeedAlias, SeedIdentity), (LinkPeerAlias, LinkPeerIdentity),
            },
            mappings: new[] { new PublishMapping(LinkPeerIdentity, LinkPeerAlias, 7000) });
        var seedAfter = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: null);
        var linksAfter = new[]
        {
            new SeedLink(SeedAlias.Value, 7000, SeedLinkTypes.DependsOn, DateTimeOffset.UnixEpoch),
        };

        var before = await SeedFingerprintCalculator.ComputeAsync(
            seedBefore, linksBefore, registryBefore, publishMapBefore);
        var after = await SeedFingerprintCalculator.ComputeAsync(
            seedAfter, linksAfter, registryAfter, publishMapAfter);

        after.ShouldBe(before);
    }

    [Fact]
    public async Task Fingerprint_link_target_to_unrelated_real_id_differs_from_mapped_peer()
    {
        // Link points at 7000, once as the peer's published id, once as an unrelated ADO
        // item that no staged seed ever produced.
        var link = new SeedLink(SeedAlias.Value, 7000, SeedLinkTypes.DependsOn, DateTimeOffset.UnixEpoch);

        var (mappedRegistry, mappedPublishMap) = Registry(
            aliases: new (StagedAlias, StagedIdentity)[]
            {
                (SeedAlias, SeedIdentity), (LinkPeerAlias, LinkPeerIdentity),
            },
            mappings: new[] { new PublishMapping(LinkPeerIdentity, LinkPeerAlias, 7000) });

        var (realRegistry, realPublishMap) = Registry(
            aliases: new[] { (SeedAlias, SeedIdentity) },
            mappings: Array.Empty<PublishMapping>());

        var seed = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: null);

        var mapped = await SeedFingerprintCalculator.ComputeAsync(
            seed, new[] { link }, mappedRegistry, mappedPublishMap);
        var real = await SeedFingerprintCalculator.ComputeAsync(
            seed, new[] { link }, realRegistry, realPublishMap);

        real.ShouldNotBe(mapped);
    }

    // ── ambiguous mappings fail closed ─────────────────────────────────────

    [Fact]
    public async Task Ambiguous_publish_mapping_throws()
    {
        // Two identities both claiming ADO id 9999 — a determinate corruption in the map.
        var otherIdentity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-0000000000b1"));
        var (registry, publishMap) = Registry(
            aliases: new[] { (SeedAlias, SeedIdentity) },
            mappings: new[]
            {
                new PublishMapping(LinkPeerIdentity, LinkPeerAlias, 9999),
                new PublishMapping(otherIdentity, Alias(-14), 9999),
            });
        var seed = BuildSeed(SeedAlias.Value, SeedIdentity, parentId: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => SeedFingerprintCalculator.ComputeAsync(
                seed, Array.Empty<SeedLink>(), registry, publishMap));
        ex.Message.ShouldContain("Ambiguous publish mapping");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static StagedAlias Alias(int negative)
    {
        StagedAlias.TryFrom(negative, out var alias).ShouldBeTrue();
        return alias;
    }

    private static WorkItem BuildSeed(int id, StagedIdentity identity, int? parentId)
    {
        return new WorkItem
        {
            Id = id,
            Title = "seed",
            Type = WorkItemType.Parse("Task").Value,
            IsSeed = true,
            StagedIdentity = identity,
            ParentId = parentId,
        };
    }

    /// <summary>
    /// Wires an <see cref="IStagedIdentityRegistry"/> that knows the given alias→identity
    /// bindings and an <see cref="IPublishIdMapRepository"/> whose <c>GetAllMappingsAsync</c>
    /// returns the supplied rows. Nothing else is stubbed — the calculator must only
    /// exercise those two seams.
    /// </summary>
    private static (IStagedIdentityRegistry Registry, IPublishIdMapRepository PublishMap) Registry(
        (StagedAlias Alias, StagedIdentity Identity)[] aliases,
        IReadOnlyList<PublishMapping> mappings)
    {
        var registry = Substitute.For<IStagedIdentityRegistry>();
        foreach (var (alias, identity) in aliases)
        {
            registry.FindByAliasAsync(alias, Arg.Any<CancellationToken>())
                .Returns((StagedIdentity?)identity);
        }
        // Every unknown alias resolves to null — that is what an unregistered id looks like.
        registry.FindByAliasAsync(
                Arg.Is<StagedAlias>(a => !aliases.Any(x => x.Alias == a)),
                Arg.Any<CancellationToken>())
            .Returns((StagedIdentity?)null);

        var publishMap = Substitute.For<IPublishIdMapRepository>();
        publishMap.GetAllMappingsAsync(Arg.Any<CancellationToken>()).Returns(mappings);
        return (registry, publishMap);
    }
}
