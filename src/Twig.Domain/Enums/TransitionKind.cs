namespace Twig.Domain.Enums;

/// <summary>
/// Classifies a state transition for routing purposes.
/// ADO enforces process-specific transition rules; twig only distinguishes
/// forward/cut for UI affordances (e.g. cut transitions may still prompt).
/// </summary>
public enum TransitionKind
{
    /// <summary>Invalid or disallowed transition — no matching rule found.</summary>
    None = 0,

    /// <summary>Toward completion or any ordinal move between non-removed states.</summary>
    Forward = 1,

    /// <summary>To Removed — a destructive cut transition.</summary>
    Cut = 3
}
