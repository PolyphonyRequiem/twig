namespace Twig.Domain.Services.Policy;

/// <summary>Outcome of evaluating one declared rule against a proposal.</summary>
public enum PolicyOutcome
{
    /// <summary>Evaluated; nothing in the proposal violates the rule.</summary>
    Permit,

    /// <summary>A violation that does not block: a warn-enforced rule, or a pre-existing violation the proposal does not worsen (D4).</summary>
    Warn,

    /// <summary>The proposal adds or worsens a violation. Blocks apply.</summary>
    Refuse,

    /// <summary>The rule could not be evaluated. Blocks apply — never a silent permit (D3).</summary>
    Unavailable,
}

/// <summary>One explained policy decision about a proposal (AB#1169).</summary>
public sealed record PolicyDecision
{
    /// <summary>The declaring rule's id, or <c>policy-load</c>.</summary>
    public required string RuleId { get; init; }

    /// <summary>The rule kind.</summary>
    public required string Kind { get; init; }

    /// <summary>The outcome.</summary>
    public required PolicyOutcome Outcome { get; init; }

    /// <summary>Human-readable explanation naming the item and the fix.</summary>
    public required string Reason { get; init; }

    /// <summary>The affected work item (negative for a seed), when the decision names one.</summary>
    public int? WorkItemId { get; init; }

    /// <summary>The proposal operation that introduced the change, when one did.</summary>
    public string? OpId { get; init; }

    /// <summary>True when this decision prevents the proposal from applying.</summary>
    public bool IsBlocking => Outcome is PolicyOutcome.Refuse or PolicyOutcome.Unavailable;

    /// <summary>Wire spelling of <see cref="Outcome"/>.</summary>
    public string OutcomeName => Outcome switch
    {
        PolicyOutcome.Permit => "permit",
        PolicyOutcome.Warn => "warn",
        PolicyOutcome.Refuse => "refuse",
        _ => "unavailable",
    };
}
