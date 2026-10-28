namespace Twig.Infrastructure.GitHub;

/// <summary>
/// The complete, fixed set of GraphQL documents the read-only GitHub adapter sends. The adapter
/// builds requests from these exact strings and <see cref="GitHubReadOnlyAuthHandler"/> admits
/// only requests whose document is one of them, so nothing else can carry a bearer token.
/// </summary>
internal static class GitHubIssueQueries
{
    public const string AuthenticatedViewer = "query { viewer { id login } }";

    public const string RepositoryDiscovery = "query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { id nameWithOwner owner { id login } } }";

    public const string SchemaMetadata = """
        query {
          issue: __type(name: "Issue") {
            kind name
            fields(includeDeprecated: true) {
              name
              type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
            }
          }
          issueState: __type(name: "IssueState") {
            kind name
            enumValues(includeDeprecated: true) { name }
          }
        }
        """;

    public const string Snapshot = """
        query($id: ID!) { node(id: $id) { __typename id ... on Issue {
          number title state body url updatedAt issueType { name } parent { id } milestone { id title } repository { id nameWithOwner owner { id } }
        } } }
        """;

    private static readonly (string Field, string Selection)[] ConnectionShapes =
    [
        ("labels", "id name"),
        ("assignees", "id login"),
        ("subIssues", "id"),
        ("blockedBy", "id"),
        ("blocking", "id"),
    ];

    private static readonly Dictionary<string, string> ConnectionDocuments = ConnectionShapes.ToDictionary(
        s => s.Field,
        s => $$"""
            query($id: ID!, $after: String) { node(id: $id) { __typename id ... on Issue {
              {{s.Field}}(first: 100, after: $after) { nodes { {{s.Selection}} } pageInfo { hasNextPage endCursor } }
            } } }
            """,
        StringComparer.Ordinal);

    private static readonly HashSet<string> PaginatedDocuments = new(ConnectionDocuments.Values, StringComparer.Ordinal);

    /// <summary>The fixed connection document for <paramref name="field"/>; unknown fields are a programming error.</summary>
    public static string Connection(string field)
        => ConnectionDocuments.TryGetValue(field, out var document)
            ? document : throw new ArgumentOutOfRangeException(nameof(field), field, "Not an allowed GitHub connection.");

    /// <summary>
    /// Ordinal match against the allowed documents. <paramref name="paginated"/> reports whether the
    /// document's variable shape is <c>{id, after}</c> rather than <c>{id}</c>.
    /// </summary>
    public static bool TryMatch(string document, out bool paginated)
    {
        paginated = PaginatedDocuments.Contains(document);
        return paginated
            || string.Equals(document, Snapshot, StringComparison.Ordinal)
            || string.Equals(document, SchemaMetadata, StringComparison.Ordinal);
    }
}
