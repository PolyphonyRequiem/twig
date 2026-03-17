namespace Twig.Domain.ValueObjects;

/// <summary>
/// Request DTO for creating an ADO pull request.
/// </summary>
public sealed record PullRequestCreate(
    string SourceBranch, string TargetBranch, string Title,
    string Description, int? WorkItemId);
