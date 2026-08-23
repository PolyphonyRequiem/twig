using System.Globalization;
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
/// <para>
/// 🔴 <b>Refuse on source revision mismatch.</b> The gate evaluates rules against a
/// specific point-in-time snapshot of the source item. If the cached source's revision
/// does not equal <see cref="BatchOperation.ExpectedRevision"/>, the "old" values the gate
/// would read are stale (or ahead of) the revision the strict-CAS PATCH is aimed at, and
/// evaluating rules under that mismatch would false-refuse valid batches or false-permit
/// invalid ones. Rather than skip the gate and let the wire 412, we surface a coherent
/// client-side refusal — the batch never touches the wire.
/// </para>
/// <para>
/// 🔴 <b>Old vs new views.</b> Generic rule verbs read from two views of the item, not one:
/// <c>whenWas</c> reads the pre-batch value, <c>when</c> reads the effective post-batch
/// value, and <c>whenChanged</c>/<c>whenNotChanged</c> compare the two. The gate builds
/// both maps up-front (canonical WorkItem properties + arbitrary Fields, then the batch
/// overlaid on top for the new view) so a non-state field's change is detectable exactly
/// the same way a state field's is — this is what fixes the AB#673 regression where a
/// <c>whenChanged Custom.Foo</c> clause was silently unreachable.
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
    /// <see cref="BatchOperation.Fields"/> and the target <c>System.State</c>, OR a refusal
    /// when the cached source is not at the batch's expected revision.
    /// </summary>
    public async Task<string?> EvaluateAsync(
        BatchOperation batch,
        WorkItem source,
        CancellationToken ct = default)
    {
        // Refuse-before-wire on revision drift: the strict-CAS PATCH will 412, but a stale
        // source would also invalidate the rule evaluation itself. Better to surface a
        // coherent client-side refusal than let the gate run on the wrong snapshot.
        if (source.Revision != batch.ExpectedRevision)
        {
            return
                $"Refusing to write work item {source.Id}: local cache is at revision "
                + $"{source.Revision.ToString(CultureInfo.InvariantCulture)} but the batch "
                + $"expects revision "
                + $"{batch.ExpectedRevision.ToString(CultureInfo.InvariantCulture)}; "
                + "refresh the cache before applying the plan.";
        }

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

        // The "old" view: canonical WorkItem properties seeded first, then the arbitrary
        // Fields dictionary overlaid so a caller who wrote System.Title via UpdateField
        // wins over the aggregate's typed property. Canonical seeding matters because the
        // WorkItem constructor accepts Title/State/AssignedTo directly WITHOUT touching
        // Fields, and a Fields-only source view would miss a rule that names any of those.
        var oldFields = BuildSourceView(source);

        // The "new" view: old with the batch's writes overlaid, and the resolved System.State
        // written unconditionally so a rule can read it whether or not the batch names it.
        var newFields = new Dictionary<string, string?>(oldFields, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in batch.Fields)
            newFields[kv.Key] = kv.Value;
        oldFields[SystemStateField] = fromState;
        newFields[SystemStateField] = toState;

        foreach (var rule in rules)
        {
            if (rule.IsDisabled) continue;
            if (!ConditionsFire(rule.Conditions, fromState, toState, oldFields, newFields))
                continue;

            foreach (var action in rule.Actions)
            {
                if (!IsMakeRequired(action.ActionType)) continue;
                var field = action.TargetField;
                if (string.IsNullOrWhiteSpace(field)) continue;
                newFields.TryGetValue(field, out var value);
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

    /// <summary>
    /// Materialises the canonical WorkItem properties into a field map, then overlays the
    /// aggregate's arbitrary Fields dictionary. Canonical entries are seeded first so that
    /// a caller that wrote a canonical field via <c>UpdateField</c> wins — the aggregate
    /// property is a floor, the Fields entry is the authored value.
    /// </summary>
    private static Dictionary<string, string?> BuildSourceView(WorkItem source)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.Id"] = source.Id.ToString(CultureInfo.InvariantCulture),
            ["System.WorkItemType"] = source.Type.Value,
            ["System.Title"] = source.Title,
            ["System.State"] = source.State,
            ["System.AssignedTo"] = source.AssignedTo,
            ["System.IterationPath"] = source.IterationPath.Value,
            ["System.AreaPath"] = source.AreaPath.Value,
            ["System.Rev"] = source.Revision.ToString(CultureInfo.InvariantCulture),
        };
        if (source.ParentId is int parentId)
            map["System.Parent"] = parentId.ToString(CultureInfo.InvariantCulture);
        foreach (var kv in source.Fields)
            map[kv.Key] = kv.Value;
        return map;
    }

    private static bool ConditionsFire(
        IReadOnlyList<RuleCondition> conditions,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> oldFields,
        IReadOnlyDictionary<string, string?> newFields)
    {
        // Conjunctive — every clause must hold, matching the assembler's own reading of a
        // rule's condition set (see ProcessDescriptionAssembler.BuildRequirednessIndex).
        foreach (var c in conditions)
            if (!MatchCondition(c, fromState, toState, oldFields, newFields))
                return false;
        return true;
    }

    /// <remarks>
    /// Vocabulary mirrors <c>DependentFieldReconciler</c> in intent, but reads BOTH the old
    /// and new views so a generic <c>whenChanged Custom.Foo</c> works exactly like
    /// <c>whenChanged System.State</c> does — the AB#673 review found the reconciler's
    /// single-map approximation false-negatived on non-state clauses. Unknown verbs return
    /// <c>false</c>: a gate that fired on a verb it did not understand would refuse valid
    /// batches, and the executor's readback still catches a wire-level violation.
    /// </remarks>
    private static bool MatchCondition(
        RuleCondition condition,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> oldFields,
        IReadOnlyDictionary<string, string?> newFields)
    {
        oldFields.TryGetValue(condition.Field, out var oldValue);
        newFields.TryGetValue(condition.Field, out var newValue);

        var verb = condition.ConditionType?.TrimStart('$') ?? string.Empty;

        if (VerbEquals(verb, "when"))
            return Equal(newValue, condition.Value);
        if (VerbEquals(verb, "whenNot"))
            return !Equal(newValue, condition.Value);
        if (VerbEquals(verb, "whenChanged"))
            return !Equal(oldValue, newValue);
        if (VerbEquals(verb, "whenNotChanged"))
            return Equal(oldValue, newValue);
        if (VerbEquals(verb, "whenWas"))
            return Equal(oldValue, condition.Value);
        if (VerbEquals(verb, "whenStateChangedTo"))
            return Equal(toState, condition.Value);
        if (VerbEquals(verb, "whenValueIsDefined"))
            return !string.IsNullOrEmpty(newValue);
        if (VerbEquals(verb, "whenValueIsNotDefined"))
            return string.IsNullOrEmpty(newValue);

        return false;
    }

    private static bool IsMakeRequired(string? actionType)
        => VerbEquals(actionType?.TrimStart('$') ?? string.Empty, "makeRequired");

    private static bool VerbEquals(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Equal(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
