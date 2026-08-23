namespace Twig.Domain.Services.Plan;

/// <summary>
/// Lifecycle state for a plan operation as tracked in the journal (durable pending.db).
/// The plan file itself never carries state — it is the immutable declaration; state
/// lives only in the journal keyed on the canonical digest.
/// </summary>
public enum PlanOperationState
{
    /// <summary>Imported from a validated plan; not yet confirmed by the user.</summary>
    Planned,

    /// <summary>User confirmed the plan; ready for apply.</summary>
    Confirmed,

    /// <summary>An apply attempt has started for this operation.</summary>
    Applying,

    /// <summary>The apply call succeeded and the outcome was recorded.</summary>
    Applied,

    /// <summary>The applied operation was verified against ADO.</summary>
    Verified,

    /// <summary>The apply attempt failed with a determinate error.</summary>
    Failed,

    /// <summary>The apply attempt returned an indeterminate outcome (retry-visible).</summary>
    Indeterminate,
}
