using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>Reads a complete provider-scoped working set, or fails without returning a partial set.</summary>
internal interface IProviderWorkingSetReader : IWorkItemSnapshotReader
{
    Task<IReadOnlyList<ProviderWorkItemSnapshot>> FetchWorkingSetAsync(CancellationToken ct = default);
}
