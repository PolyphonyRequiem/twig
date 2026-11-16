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
}
