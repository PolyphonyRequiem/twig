using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Process;

internal static class DependentFieldReconciler
{
    public static IReadOnlyList<FieldChange> GetSafeClears(
        IReadOnlyList<ProcessRule> rules,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> currentFields)
    {
        var actions = rules
            .Where(rule => !rule.IsDisabled &&
                rule.Conditions.All(condition => Matches(condition, fromState, toState, currentFields)))
            .SelectMany(rule => rule.Actions)
            .GroupBy(action => action.TargetField, StringComparer.OrdinalIgnoreCase);
        var clears = new List<FieldChange>();

        foreach (var fieldActions in actions)
        {
            if (fieldActions.Any(action => !RuleTypeEquals(action.ActionType, "disallowValue")) ||
                !currentFields.TryGetValue(fieldActions.Key, out var currentValue))
            {
                continue;
            }

            if (fieldActions.Any(action =>
                    RuleTypeEquals(action.ActionType, "disallowValue") &&
                    string.Equals(currentValue, action.Value, StringComparison.OrdinalIgnoreCase)))
            {
                clears.Add(new FieldChange(fieldActions.Key, currentValue, null));
            }
        }

        return clears;
    }

    private static bool Matches(
        RuleCondition condition,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> currentFields)
    {
        var isState = string.Equals(condition.Field, "System.State", StringComparison.OrdinalIgnoreCase);
        currentFields.TryGetValue(condition.Field, out var currentValue);
        if (isState)
            currentValue = fromState;

        if (RuleTypeEquals(condition.ConditionType, "when"))
            return Equal(isState ? toState : currentValue, condition.Value);
        if (RuleTypeEquals(condition.ConditionType, "whenNot"))
            return !Equal(isState ? toState : currentValue, condition.Value);
        if (RuleTypeEquals(condition.ConditionType, "whenChanged"))
            return isState;
        if (RuleTypeEquals(condition.ConditionType, "whenNotChanged"))
            return !isState;
        if (RuleTypeEquals(condition.ConditionType, "whenWas"))
            return Equal(isState ? fromState : currentValue, condition.Value);
        if (RuleTypeEquals(condition.ConditionType, "whenStateChangedTo"))
            return Equal(toState, condition.Value);
        if (RuleTypeEquals(condition.ConditionType, "whenValueIsDefined"))
            return !string.IsNullOrEmpty(currentValue);
        if (RuleTypeEquals(condition.ConditionType, "whenValueIsNotDefined"))
            return string.IsNullOrEmpty(currentValue);

        return false;
    }

    private static bool RuleTypeEquals(string actual, string expected) =>
        string.Equals(actual.TrimStart('$'), expected, StringComparison.OrdinalIgnoreCase);

    private static bool Equal(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
