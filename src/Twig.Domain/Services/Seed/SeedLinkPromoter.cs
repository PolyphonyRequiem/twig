using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Navigation;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Promotes virtual seed links to ADO relations after a seed is published.
/// Called during <see cref="SeedPublishOrchestrator.PublishAsync"/> after the ID remap step.
/// </summary>
public sealed class SeedLinkPromoter
{
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IAdoWorkItemService _adoService;

    public SeedLinkPromoter(
        ISeedLinkRepository seedLinkRepo,
        IAdoWorkItemService adoService)
    {
        _seedLinkRepo = seedLinkRepo;
        _adoService = adoService;
    }

    /// <summary>
    /// Promotes all eligible seed links for the newly published item to ADO relations.
    /// Returns a list of warnings for any link promotion failures.
    /// </summary>
    /// <param name="newId">The positive ADO ID assigned to the newly published seed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<string>> PromoteLinksAsync(int newId, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        var links = await _seedLinkRepo.GetLinksForItemAsync(newId, ct);

        foreach (var link in links)
        {
            // Skip parent-child links where the newly published item is the child
            // (parent relation is already set by MapSeedToCreatePayload at creation time).
            if (link.LinkType == SeedLinkTypes.ParentChild && link.SourceId == newId)
                continue;

            // Only promote when both endpoints have positive IDs
            if (link.SourceId <= 0 || link.TargetId <= 0)
                continue;

            if (!SeedLinkTypeMapper.TryToAdoRelationType(link.LinkType, out var adoLinkType))
            {
                warnings.Add($"Unknown link type '{link.LinkType}' between {link.SourceId} and {link.TargetId}; skipped.");
                continue;
            }

            try
            {
                await _adoService.AddLinkAsync(link.SourceId, link.TargetId, adoLinkType, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to create ADO link ({link.LinkType}) between {link.SourceId} and {link.TargetId}: {ex.Message}");
            }
        }

        return warnings;
    }
}
