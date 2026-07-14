namespace Twig.Domain.ValueObjects;

internal sealed record ProcessRule(
    IReadOnlyList<RuleCondition> Conditions,
    IReadOnlyList<RuleAction> Actions,
    bool IsDisabled);

internal sealed record RuleCondition(string ConditionType, string Field, string? Value);

internal sealed record RuleAction(string ActionType, string TargetField, string? Value);
