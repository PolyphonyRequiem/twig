using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Default type-eligibility resolver. Consults the active profile's primary-scope
/// allow-set (as materialized into <see cref="WorkspaceConfig.PrimaryScopeTypes"/>
/// from the profile pinned in <c>twig.json</c>) and returns
/// <see cref="Result{T}"/>-carrying <c>true</c> only when the type is in the
/// set.
/// <para>
/// Fails closed when the allow-set cannot be resolved: an empty or missing set
/// surfaces <c>eligibility-unavailable</c> (see
/// <see cref="AttachmentStorageFailure.EligibilityUnavailable"/>). Silent
/// permit-all — the defect the review flagged — would let every managed
/// worktree without an explicit workspace policy attach any type; a repository
/// that has never bound a profile MUST refuse rather than adopt the
/// convention-less path. Type-name comparison is case-insensitive, mirroring
/// <c>StatePairComparer</c> and <c>WorkItemTypeComparer</c>.
/// </para>
/// </summary>
internal sealed class ConfigPrimaryScopeTypeEligibility : IPrimaryScopeTypeEligibility
{
    private readonly TwigConfiguration _config;

    public ConfigPrimaryScopeTypeEligibility(TwigConfiguration config)
    {
        _config = config;
    }

    public Result<bool> Evaluate(WorkItemType type)
    {
        var allow = _config.Workspace?.PrimaryScopeTypes;
        if (allow is null || allow.Count == 0)
            return Result.Fail<bool>(AttachmentStorageFailure.EligibilityUnavailable);

        for (var i = 0; i < allow.Count; i++)
        {
            if (string.Equals(allow[i], type.Value, StringComparison.OrdinalIgnoreCase))
                return Result.Ok(true);
        }
        return Result.Ok(false);
    }
}
