using Twig.Domain.Services.Plan;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Prepares fixed request shapes only. This is not transport admission, repository membership
/// evidence, or revision protection; the provider-v2 executor remains disabled.
/// </summary>
internal static class GitHubRelationshipMutations
{
    private const string AddSubIssue = "mutation($issueId: ID!, $subIssueId: ID!) { addSubIssue(input: {issueId: $issueId, subIssueId: $subIssueId, replaceParent: false}) { clientMutationId } }";

    private const string RemoveSubIssue = "mutation($issueId: ID!, $subIssueId: ID!) { removeSubIssue(input: {issueId: $issueId, subIssueId: $subIssueId}) { clientMutationId } }";

    private const string AddBlockedBy = "mutation($issueId: ID!, $blockingIssueId: ID!) { addBlockedBy(input: {issueId: $issueId, blockingIssueId: $blockingIssueId}) { clientMutationId } }";

    private const string RemoveBlockedBy = "mutation($issueId: ID!, $blockingIssueId: ID!) { removeBlockedBy(input: {issueId: $issueId, blockingIssueId: $blockingIssueId}) { clientMutationId } }";

    public static GitHubIssueGraphQlRequest Create(
        ProviderMutationOperation operation, string resourceScope)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceScope);
        if (operation.OperationKind is not (PlanOperationKind.AddLink or PlanOperationKind.RemoveLink)
            || operation.Other is null || operation.Fields is not null)
            throw new ArgumentException("Expected a provider link operation with a peer and no fields.", nameof(operation));

        var source = operation.Target.Item.ExternalIdentity;
        var other = operation.Other.ExternalIdentity;
        if (source.ProviderKind != "github" || other.ProviderKind != "github"
            || source.ResourceScope != resourceScope || other.ResourceScope != resourceScope)
            throw new ArgumentException("Both endpoints must belong to this GitHub API origin.", nameof(operation));
        if (source == other || operation.Target.Item.LocalId == operation.Other.LocalId)
            throw new ArgumentException("A relationship requires distinct endpoints.", nameof(operation));

        var adding = operation.OperationKind == PlanOperationKind.AddLink;
        return operation.Relation switch
        {
            "parent" => new(adding ? AddSubIssue : RemoveSubIssue,
                new() { ["issueId"] = other.NativeId, ["subIssueId"] = source.NativeId }),
            "predecessor" => new(adding ? AddBlockedBy : RemoveBlockedBy,
                new() { ["issueId"] = source.NativeId, ["blockingIssueId"] = other.NativeId }),
            "successor" => new(adding ? AddBlockedBy : RemoveBlockedBy,
                new() { ["issueId"] = other.NativeId, ["blockingIssueId"] = source.NativeId }),
            _ => throw new ArgumentException("Only parent, predecessor and successor are supported.", nameof(operation)),
        };
    }
}
