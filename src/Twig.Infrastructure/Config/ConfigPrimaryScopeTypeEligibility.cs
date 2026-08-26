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
/// Concrete checked-in profile policy source: reads the AB#736 §4.1 policy
/// block from <c>twig.json</c> (via <see cref="TwigConfiguration.Policy"/>).
/// Missing block or missing <see cref="PolicyConfig.PrimaryScopeTypes"/> fails
/// closed with <c>eligibility-unavailable</c> so a repository that has never
/// published a policy never silently permits arbitrary primary-scope types.
/// AB#727 will introduce a profile-registry source that plugs into the same
/// <see cref="IPrimaryScopePolicySource"/> seam.
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
        var types = _config.Policy?.PrimaryScopeTypes;
        if (types is null)
            return Result.Fail<IReadOnlyList<string>>(AttachmentStorageFailure.EligibilityUnavailable);
        return Result.Ok<IReadOnlyList<string>>(types);
    }
}
