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
/// The T1 §8.1 cutover: <see cref="IPrimaryScopePolicySource"/> is "a thin
/// adapter over <c>IReferenceProfileProvider.PrimaryScopeAllowTypeNames</c> —
/// no independent policy source remains".
/// </summary>
/// <remarks>
/// <para>
/// This replaces the checked-in policy source that read
/// <c>twig.json</c>'s <c>policy.primaryScopeTypes</c> as the authority. That
/// list is a MATERIALIZATION of the pinned profile written at init, so treating
/// it as authoritative made a hand-edited manifest able to widen the allow-set
/// past what the profile declares — with no signal, because a widened list is
/// indistinguishable from a correctly materialized one. T1 §3.6 is explicit
/// that narrowing the allow-set means publishing a different profile identity,
/// not editing a repository file, so the profile is the only correct authority.
/// </para>
/// <para>
/// 🔴 The pin is validated BEFORE the allow-set is read. Returning an allow-set
/// from a profile release the repository is not pinned to would answer the
/// eligibility question from the wrong document while looking entirely healthy
/// — the drift would surface later as an unexplained attach refusal or, worse,
/// an attach that should have been refused. Checking the pin here is what makes
/// T1 §6.1 a production gate rather than a documented intention.
/// </para>
/// </remarks>
internal sealed class ReferenceProfilePolicySource(IReferenceProfileProvider profileProvider)
    : IPrimaryScopePolicySource
{
    private readonly IReferenceProfileProvider _profileProvider = profileProvider;

    public Result<IReadOnlyList<string>> GetAllowSet()
    {
        var pin = _profileProvider.ValidatePin();
        if (!pin.IsSuccess)
            return Result.Fail<IReadOnlyList<string>>(pin.Error);

        var loaded = _profileProvider.Load();
        if (!loaded.IsSuccess)
            return Result.Fail<IReadOnlyList<string>>(loaded.Error);

        // Propagate the profile's own named identifier rather than flattening to
        // eligibility-unavailable: "the profile blob is corrupt" and "this
        // repository pins a version this binary does not ship" need different
        // repairs, and only the specific identifier says which.
        return Result.Ok(loaded.Value.PrimaryScopeAllowTypeNames);
    }
}
