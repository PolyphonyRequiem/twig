using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for persisting and querying virtual seed links.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface ISeedLinkRepository
{
    Task AddLinkAsync(SeedLink link, CancellationToken ct = default);
    Task RemoveLinkAsync(int sourceId, int targetId, string linkType, CancellationToken ct = default);
    Task<IReadOnlyList<SeedLink>> GetLinksForItemAsync(int itemId, CancellationToken ct = default);
    Task<IReadOnlyList<SeedLink>> GetAllSeedLinksAsync(CancellationToken ct = default);
    Task DeleteLinksForItemAsync(int itemId, CancellationToken ct = default);
}
