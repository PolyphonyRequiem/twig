using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>Reads provider-native snapshots without assigning local identities or persisting data.</summary>
internal interface IWorkItemSnapshotReader : IWorkItemProvider
{
    Task<ProviderWorkItemSnapshot> FetchSnapshotAsync(
        ExternalWorkItemIdentity identity, CancellationToken ct = default);
}
