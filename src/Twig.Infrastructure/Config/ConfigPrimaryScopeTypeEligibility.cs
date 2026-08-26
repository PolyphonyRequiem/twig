using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Default type-eligibility resolver: consults the workspace's configured
/// primary-scope allow-set and returns <c>true</c> when the type is in the
/// set. An empty or missing set is permissive: every type is eligible. This
/// matches the "runtime policy/profile data" contract in AB#738 — the gate
/// exists to be tightened by configuration, not to hard-code any allow-list.
/// <para>
/// The allow-set lives on <see cref="WorkspaceConfig.PrimaryScopeTypes"/>. It is
/// process-agnostic — a value is any string the process description recognises
/// as a work-item type. Case-insensitive comparison mirrors the rest of the
/// codebase (see <c>StatePairComparer</c>, <c>WorkItemTypeComparer</c>).
/// </para>
/// </summary>
internal sealed class ConfigPrimaryScopeTypeEligibility : IPrimaryScopeTypeEligibility
{
    private readonly TwigConfiguration _config;

    public ConfigPrimaryScopeTypeEligibility(TwigConfiguration config)
    {
        _config = config;
    }

    public bool IsEligible(WorkItemType type)
    {
        var allow = _config.Workspace?.PrimaryScopeTypes;
        if (allow is null || allow.Count == 0)
            return true;

        for (var i = 0; i < allow.Count; i++)
        {
            if (string.Equals(allow[i], type.Value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
