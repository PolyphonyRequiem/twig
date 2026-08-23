using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// Runtime, process-agnostic gate over a plan <see cref="BatchOperation"/> that refuses the
/// write BEFORE any ADO PATCH when the batch would land the target work item in a state
/// where an enabled <c>makeRequired</c> ProcessRule applies but the effective value for the
/// rule's target field is empty.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Why this is a lifecycle-level concern.</b> A rule-based Done gate (e.g. <i>when
/// State = Done → makeRequired X</i>) is a <b>state rule</b>, not a transition restriction:
/// the server evaluates it after the write and — verified live against Hyperbright while
/// resolving AB#673 — a <c>bypassRules=true</c> PATCH walks past it, closing a work item
/// with the gate fields empty and answering HTTP 200. Twig runs as a privileged automation
/// identity and cannot afford to hand back "verified" for a write that silently walked past
/// the process's own gate. So the plan surface enforces the gate on the client side, before
/// touching the wire, on top of whatever ADO does or does not enforce.
/// </para>
/// <para>
/// 🔴 <b>Process-agnostic on purpose.</b> This class never names a work-item type, a state,
/// or a custom field — it reads the actual rule set for the source item's type at apply
/// time and evaluates it as data. That is what lets the same enforcement cover Bug→Done,
/// Task→Done, an Epic gate a caller invents next month, and every rule inherited from a
/// derived process, without a code change.
/// </para>
/// <para>
/// 🔴 <b>Fail-open on rule-load errors.</b> A gate that flipped a plan to Failed because the
/// rules endpoint 500'd would confuse a policy decision with a fetch error. If the provider
/// throws (or returns nothing), the gate returns <c>null</c> and the executor proceeds — the
/// executor's own readback still catches a wire-level 412/404. A missing local source
/// (<see cref="IWorkItemRepository.GetByIdAsync"/> returning <c>null</c>) is handled the
/// same way at the call site: without an aggregate to overlay we cannot honestly evaluate.
/// </para>
/// </remarks>
internal sealed class PlanProcessRuleGate
{
    private readonly IProcessRuleProvider? _rules;

    /// <summary>
    /// Constructs a gate over the given rule provider. A <c>null</c> provider produces a
    /// gate that always permits — the same shape a DI graph missing the network layer
    /// would produce, and the state <see cref="PlanLifecycleService"/> should still be
    /// usable in.
    /// </summary>
    public PlanProcessRuleGate(IProcessRuleProvider? rules) => _rules = rules;

    /// <summary>
    /// Returns <c>null</c> when <paramref name="batch"/> may proceed, else a refusal message
    /// naming the first field an enabled <c>makeRequired</c> rule requires but the effective
    /// value is empty after <paramref name="source"/> is overlaid by
    /// <see cref="BatchOperation.Fields"/> and the target <c>System.State</c>.
    /// </summary>
    public async Task<string?> EvaluateAsync(
        BatchOperation batch,
        WorkItem source,
        CancellationToken ct = default)
    {
        if (_rules is null) return null;

        var typeName = source.Type.Value;
        if (string.IsNullOrEmpty(typeName)) return null;

        IReadOnlyList<ProcessRule> rules;
        try
        {
            rules = await _rules.GetRulesAsync(typeName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Rule-load failure is not a policy decision; see class remarks.
            return null;
        }
        if (rules is null || rules.Count == 0) return null;

        var fromState = source.State ?? string.Empty;
        var toState = fromState;
        if (batch.Fields.TryGetValue(SystemStateField, out var stateOverride)
            && !string.IsNullOrEmpty(stateOverride))
        {
            toState = stateOverride!;
        }

        var effective = new Dictionary<string, string?>(source.Fields, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in batch.Fields)
            effective[kv.Key] = kv.Value;

        // Every rule reads System.State off `effective` too — the batch may or may not
        // include the field explicitly, so we mirror the resolved toState onto the map.
        effective[SystemStateField] = toState;

        foreach (var rule in rules)
        {
            if (rule.IsDisabled) continue;
            if (!ConditionsFire(rule.Conditions, fromState, toState, effective))
                continue;

            foreach (var action in rule.Actions)
            {
                if (!IsMakeRequired(action.ActionType)) continue;
                var field = action.TargetField;
                if (string.IsNullOrWhiteSpace(field)) continue;
                effective.TryGetValue(field, out var value);
                if (string.IsNullOrEmpty(value))
                {
                    return
                        $"Refusing to write work item {source.Id}: an enabled process rule requires "
                        + $"field '{field}' when state is '{toState}', but the effective value is empty.";
                }
            }
        }

        return null;
    }

    private const string SystemStateField = "System.State";

    private static bool ConditionsFire(
        IReadOnlyList<RuleCondition> conditions,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> current)
    {
        // Conjunctive — every clause must hold, matching the assembler's own reading of a
        // rule's condition set (see ProcessDescriptionAssembler.BuildRequirednessIndex).
        foreach (var c in conditions)
            if (!MatchCondition(c, fromState, toState, current))
                return false;
        return true;
    }

    /// <remarks>
    /// Vocabulary mirrors <c>DependentFieldReconciler</c> so the two gates read the same
    /// rule sets identically. Unknown verbs return <c>false</c> — a gate that fired on a
    /// verb it did not understand would refuse valid batches; the executor's readback
    /// still catches a wire-level violation, so a false negative here is the safer error.
    /// </remarks>
    private static bool MatchCondition(
        RuleCondition condition,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> currentFields)
    {
        var isState = string.Equals(condition.Field, SystemStateField, StringComparison.OrdinalIgnoreCase);
        currentFields.TryGetValue(condition.Field, out var currentValue);
        if (isState) currentValue = fromState;

        var verb = condition.ConditionType?.TrimStart('$') ?? string.Empty;

        if (VerbEquals(verb, "when"))
            return Equal(isState ? toState : currentValue, condition.Value);
        if (VerbEquals(verb, "whenNot"))
            return !Equal(isState ? toState : currentValue, condition.Value);
        if (VerbEquals(verb, "whenChanged"))
            return isState && !Equal(fromState, toState);
        if (VerbEquals(verb, "whenNotChanged"))
            return !(isState && !Equal(fromState, toState));
        if (VerbEquals(verb, "whenWas"))
            return Equal(isState ? fromState : currentValue, condition.Value);
        if (VerbEquals(verb, "whenStateChangedTo"))
            return Equal(toState, condition.Value);
        if (VerbEquals(verb, "whenValueIsDefined"))
            return !string.IsNullOrEmpty(currentValue);
        if (VerbEquals(verb, "whenValueIsNotDefined"))
            return string.IsNullOrEmpty(currentValue);

        return false;
    }

    private static bool IsMakeRequired(string? actionType)
        => VerbEquals(actionType?.TrimStart('$') ?? string.Empty, "makeRequired");

    private static bool VerbEquals(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Equal(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
