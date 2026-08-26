using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Default eligibility resolver — delegates to the injected
/// <see cref="IPrimaryScopePolicySource"/> for the allow-set. Case-insensitive
/// match, mirroring <c>StatePairComparer</c> / <c>WorkItemTypeComparer</c>.
/// A failure result from the source (no policy yet published) propagates
/// unchanged so the attachment service surfaces
/// <see cref="AttachmentFailure.EligibilityUnavailable"/>.
/// </summary>
internal sealed class ConfigPrimaryScopeTypeEligibility : IPrimaryScopeTypeEligibility
{
    private readonly IPrimaryScopePolicySource _source;

    public ConfigPrimaryScopeTypeEligibility(IPrimaryScopePolicySource source)
    {
        _source = source;
    }

    public Result<bool> Evaluate(WorkItemType type)
    {
        var allow = _source.GetAllowSet();
        if (!allow.IsSuccess)
            return Result.Fail<bool>(allow.Error);

        var set = allow.Value;
        for (var i = 0; i < set.Count; i++)
        {
            if (string.Equals(set[i], type.Value, StringComparison.OrdinalIgnoreCase))
                return Result.Ok(true);
        }
        return Result.Ok(false);
    }
}

/// <summary>
/// Checked-in profile policy source: reads the AB#736 §4.1 policy block from
/// <c>twig.json</c> as the materialized policy of the pinned profile. The
/// block MUST carry a <see cref="SelectedProfileBinding"/> with non-empty
/// identity + version AND a non-null <see cref="PolicyConfig.PrimaryScopeTypes"/>
/// list; any missing or hand-clipped field fails closed with
/// <c>eligibility-unavailable</c> so a partial migration surfaces at the
/// eligibility gate rather than silently permitting types.
/// <para>
/// This source is authoritative today. AB#727 will introduce a profile
/// registry that supplies the same <see cref="IPrimaryScopePolicySource"/>
/// seam; the switchover is a single-line DI change. There is no permanently
/// unavailable default — <c>twig init</c> materializes a policy block on
/// every managed init, so the "block absent" case is a migration event, not
/// the normal steady state.
/// </para>
/// </summary>
internal sealed class CheckedInProfilePolicySource : IPrimaryScopePolicySource
{
    private readonly TwigConfiguration _config;

    public CheckedInProfilePolicySource(TwigConfiguration config)
    {
        _config = config;
    }

    public Result<IReadOnlyList<string>> GetAllowSet()
    {
        var policy = _config.Policy;
        if (policy is null)
            return Result.Fail<IReadOnlyList<string>>(AttachmentStorageFailure.EligibilityUnavailable);

        // The block MUST bind a selected profile identity and version — that
        // is the materialized side of what AB#727 will publish independently.
        // A hand-clipped identity or missing version means the manifest is
        // out of contract and eligibility fails closed.
        var binding = policy.SelectedProfile;
        if (binding is null
            || string.IsNullOrWhiteSpace(binding.Identity)
            || string.IsNullOrWhiteSpace(binding.Version))
        {
            return Result.Fail<IReadOnlyList<string>>(AttachmentStorageFailure.EligibilityUnavailable);
        }

        var types = policy.PrimaryScopeTypes;
        if (types is null)
            return Result.Fail<IReadOnlyList<string>>(AttachmentStorageFailure.EligibilityUnavailable);
        return Result.Ok<IReadOnlyList<string>>(types);
    }
}
