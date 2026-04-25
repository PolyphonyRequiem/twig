namespace Twig.Domain.ValueObjects;

/// <summary>
/// Read-only representation of an ADO pull request.
/// </summary>
public sealed record PullRequestInfo(
    int PullRequestId, string Title, string Status,
    string SourceBranch, string TargetBranch, string Url);
