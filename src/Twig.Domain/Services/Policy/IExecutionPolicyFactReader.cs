using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Policy;

/// <summary>Reads complete native relationships, rejecting malformed evidence before projection.</summary>
internal interface IExecutionPolicyFactReader
{
    Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchPolicyFactsAsync(
        IReadOnlyList<int> ids, CancellationToken ct);
}
