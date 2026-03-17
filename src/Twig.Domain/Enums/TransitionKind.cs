namespace Twig.Domain.Enums;

/// <summary>
/// Classifies a state transition for confirmation requirements.
/// </summary>
public enum TransitionKind
{
    /// <summary>Invalid or disallowed transition — no matching rule found.</summary>
    None = 0,

    /// <summary>Toward completion — auto-applies without confirmation.</summary>
    Forward = 1,

    /// <summary>Toward earlier state — requires confirmation.</summary>
    Backward = 2,

    /// <summary>To Removed — requires confirmation and reason.</summary>
    Cut = 3
}
