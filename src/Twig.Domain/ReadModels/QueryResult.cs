using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Immutable read model carrying WIQL query results.
/// <see cref="IsTruncated"/> is a best-effort heuristic: true when the
/// result count equals the requested <c>$top</c> limit, indicating that
/// additional matches may exist on the server.
/// <see cref="Query"/> is a human-readable description of the active filters
/// (e.g. "title contains 'keyword' AND state = 'Doing'"), defaulting to "all items".
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<WorkItem> Items,
    bool IsTruncated,
    string Query = "all items");
