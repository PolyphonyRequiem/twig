using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Immutable read model carrying WIQL query results.
/// <see cref="IsTruncated"/> is a best-effort heuristic: true when the
/// result count equals the requested <c>$top</c> limit, indicating that
/// additional matches may exist on the server.
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<WorkItem> Items,
    bool IsTruncated);
