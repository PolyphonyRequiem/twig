using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// ADO adapter for <see cref="IProviderRevisionBoundWriter"/> (Feature #1178 AC (b)). It maps
/// the neutral relation to ADO's link type and refuses the opaque precondition before any
/// transport call, then delegates to the revision-bound ADO client unchanged, so requests stay
/// byte-identical.
/// </summary>
internal sealed class AdoProviderRevisionBoundWriter(IRevisionBoundAdoWorkItemService ado) : IProviderRevisionBoundWriter
{
    private static AdoProviderRevisionCodec Codec => AdoProviderRevisionCodec.Instance;

    public async Task<ProviderRevisionToken> AddRelationshipAsync(
        int sourceId, ProposalRelation relation, int targetId, ProviderRevisionPrecondition expected, CancellationToken ct = default)
    {
        var adoRelation = ToAdoRelation(relation);
        var expectedRevision = Codec.DecodePrecondition(expected);
        var newRevision = await ado.AddLinkAtRevisionAsync(sourceId, adoRelation, targetId, expectedRevision, ct)
            .ConfigureAwait(false);
        return Codec.EncodeWrittenRevision(newRevision);
    }

    public async Task<ProviderRevisionToken> RemoveRelationshipAsync(
        int sourceId, ProposalRelation relation, int targetId, ProviderRevisionPrecondition expected, CancellationToken ct = default)
    {
        var adoRelation = ToAdoRelation(relation);
        var expectedRevision = Codec.DecodePrecondition(expected);
        var newRevision = await ado.RemoveLinkAtRevisionAsync(sourceId, adoRelation, targetId, expectedRevision, ct)
            .ConfigureAwait(false);
        return Codec.EncodeWrittenRevision(newRevision);
    }

    public async Task DeleteAsync(int id, ProviderRevisionPrecondition expected, CancellationToken ct = default)
    {
        var expectedRevision = Codec.DecodePrecondition(expected);
        await ado.DeleteAtRevisionAsync(id, expectedRevision, ct).ConfigureAwait(false);
    }

    // ProposalRelation is a closed set that LinkTypeMapper covers in full (pinned by test), so
    // an unmapped member is a programming defect, never user input.
    private static string ToAdoRelation(ProposalRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return LinkTypeMapper.TryResolve(relation.Name, out var adoRelation)
            ? adoRelation
            : throw new InvalidOperationException($"ADO has no link type for proposal relation '{relation.Name}'.");
    }
}

/// <summary>
/// ADO adapter for <see cref="IProviderSeedPublisher"/> (Feature #1178 AC (b)). The seed
/// publish orchestrator is resolved only when a seed is actually published, so composing
/// proposal apply never builds it.
/// </summary>
internal sealed class AdoProviderSeedPublisher(Func<SeedPublishOrchestrator> orchestrator) : IProviderSeedPublisher
{
    public Task<SeedPublishResult> PublishAsync(int seedId, CancellationToken ct = default)
        => orchestrator().PublishAsync(seedId, force: false, dryRun: false, ct);
}
