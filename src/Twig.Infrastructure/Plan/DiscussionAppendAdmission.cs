using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Plan;

internal sealed class DiscussionAppendAdmission(
    CurrentBenchResolver benches,
    BenchEvaluator evaluator,
    IAuthenticationProvider authentication,
    IWorkItemRepository workItems)
{
    internal async Task<string?> ValidateAsync(AppendCommentOperation operation, CancellationToken ct)
    {
        if (authentication is not IBoundAuthenticationMetadata metadata)
            return "Bound identity metadata is unavailable.";
        var identity = await metadata.GetBoundIdentityAsync(ct).ConfigureAwait(false);
        if (!string.Equals(identity.IdentityId, operation.ExpectedIdentityId, StringComparison.Ordinal))
            return "The bound identity changed since the discussion append was reviewed.";
        if (string.IsNullOrWhiteSpace(identity.AccountName))
            return "The bound identity has no canonical account for saved-Bench membership.";

        var (effective, stored) = await benches.ResolveExplicitAsync(
            operation.SavedBenchId, operation.SavedBenchId, identity.AccountName, ct).ConfigureAwait(false);
        if (!string.Equals(BenchQueryRule.ContentsDigest(stored.Selectors),
                operation.ExpectedBenchContentsDigest, StringComparison.Ordinal))
            return "The saved Bench contents changed since the discussion append was reviewed.";
        var membership = await evaluator.EvaluateAsync(effective, ct: ct).ConfigureAwait(false);
        if (!membership.AllIds.Contains(operation.WorkItemId))
            return "The target work item is no longer a member of the explicitly selected saved Bench.";
        if (await workItems.GetByIdAsync(operation.WorkItemId, ct).ConfigureAwait(false) is null)
            return "The target work item is not present in the local cache.";
        return null;
    }
}
