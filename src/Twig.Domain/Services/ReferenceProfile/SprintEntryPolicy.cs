using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.ReferenceProfile;

/// <summary>
/// The production sprint-entry gate: refuses to commit a work item to a sprint
/// iteration unless its type is the reference profile's sprint-tier binding.
/// </summary>
/// <remarks>
/// <para>
/// This is the "sprint-entry-only-for-<c>task</c>" property from T1 §Locked
/// vocabulary and §3.3, answered through the T3 seam rather than by a literal
/// type name. T1 §3.3 is explicit that the lookup is answered by the reference
/// profile and not by the live process, "so the invariant holds even if the
/// live process is misconfigured" — which is why this gate consults
/// <see cref="IReferenceProfileProvider"/> and never
/// <see cref="IProcessConfigurationProvider"/>.
/// </para>
/// <para>
/// 🔴 <b>What counts as a sprint commitment.</b> An iteration path naming only
/// the project root is the backlog, not a sprint: every item Twig creates gets
/// a root iteration by default, so gating on "has an iteration path" would
/// refuse every non-Task item Twig has ever created. A path with at least one
/// child segment names a specific iteration node, and committing to a specific
/// iteration IS the sprint commitment. That is ADO's own model — the root
/// iteration is the backlog and its children are the sprints — so the
/// distinction is read off the data rather than configured.
/// </para>
/// <para>
/// 🔴 <b>The gate applies only where the repository declared the reference
/// process.</b> "Sprint entry is Task-only" is a rule of the T1 reference
/// process, not of ADO — on Agile a User Story in a sprint is the normal case,
/// and on Scrum so is a Product Backlog Item. Twig is process-agnostic, so it
/// has no standing to impose one process's structural rule on a workspace bound
/// to another. The <c>twig.json</c> profile pin is precisely the repository's
/// declaration that it runs this reference process, so its PRESENCE is the
/// condition under which the invariant is Twig's to enforce.
/// </para>
/// <para>
/// 🔴 <b>Absent and broken are different, and only one of them is a pass.</b>
/// A repository with no <c>profile</c> block never claimed this process, so the
/// write proceeds untouched. A repository whose block is PRESENT but does not
/// satisfy T1 §6.1 — wrong identity, wrong profile version, wrong base-process
/// version — has claimed it, and Twig cannot tell which release's rules apply;
/// that refuses with the pin's own identifier. Collapsing the two would let a
/// one-character typo in a version string silently disable a structural gate,
/// which is the worst possible failure mode for a guard: absent, and looking
/// present. This is why the check reads the specific identifier rather than
/// just <c>IsSuccess</c>.
/// </para>
/// <para>
/// 🔴 <b>Within scope it fails closed.</b> Once a repository HAS declared the
/// binding, a profile that then refuses to load is a broken install, not a
/// licence to skip the check — the write is refused with the profile's own
/// named identifier.
/// </para>
/// </remarks>
public sealed class SprintEntryPolicy(IReferenceProfileProvider profileProvider)
{
    private readonly IReferenceProfileProvider _profileProvider = profileProvider;

    /// <summary>
    /// Evaluates a proposed (type, iteration) commitment.
    /// </summary>
    /// <returns>
    /// <c>Ok</c> when the iteration is not a sprint commitment, when this
    /// repository declares no <c>profile</c> block at all, or when
    /// <paramref name="type"/> is the profile's sprint-tier binding. Otherwise a
    /// failure carrying <c>sprint-entry-not-sprint-tier</c>, or the named pin or
    /// profile error when a repository that DID declare the binding cannot have
    /// it evaluated.
    /// </returns>
    public Result Evaluate(WorkItemType type, IterationPath iteration)
    {
        if (!IsSprintCommitment(iteration))
            return Result.Ok();

        var pin = _profileProvider.ValidatePin();
        if (!pin.IsSuccess)
        {
            // Read the identifier, not just the boolean. Only a wholly absent
            // declaration puts this repository outside the reference process's
            // scope; a present-but-unsatisfiable one is a broken declaration and
            // must refuse rather than quietly disable the gate.
            return string.Equals(
                pin.Error, ReferenceProfileErrors.TwigJsonProfileBlockMissing, StringComparison.Ordinal)
                ? Result.Ok()
                : Result.Fail(pin.Error);
        }

        var loaded = _profileProvider.Load();
        if (!loaded.IsSuccess) return Result.Fail(loaded.Error);

        // Case-insensitive, matching WorkItemTypeComparer and T1 §3.3's rule for
        // comparing declared type names against live System.WorkItemType values.
        return string.Equals(loaded.Value.SprintTierTypeName, type.Value, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Result.Fail(SprintEntryFailure.NotSprintTier);
    }

    /// <summary>
    /// Whether <paramref name="iteration"/> names an iteration node below the
    /// project root — i.e. a sprint rather than the backlog.
    /// </summary>
    internal static bool IsSprintCommitment(IterationPath iteration) =>
        !string.IsNullOrEmpty(iteration.Value) && iteration.Value.Contains('\\', StringComparison.Ordinal);
}
