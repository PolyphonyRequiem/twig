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
/// 🔴 <b>Authoritative input, no cache path.</b> This gate is only ever handed a
/// <see cref="Twig.Domain.Aggregates.WorkItem"/> projected from an authoritative
/// point-in-time server snapshot at exactly the batch's <c>expectedRevision</c> (either
/// freshly fetched by the lifecycle or carried forward from a prior verified operation on
/// the same work item within the same apply — AB#721). The old exploratory read from the
/// filtered local cache projection has been retired; the lifecycle owns snapshot
/// availability and returns a retryable precondition upstream when it cannot obtain one.
/// </para>
/// <para>
/// 🔴 <b>Rule-load errors do not become policy decisions.</b> If the provider cannot load
/// rules, this evaluator returns permit-all; a policy refusal on a load failure would
/// deny valid batches for a transient reason.
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
internal readonly record struct PlanProcessRuleGateOutcome(
    PlanProcessRuleGateOutcomeKind Kind,
    string? Message)
{
    public static PlanProcessRuleGateOutcome Ok => default;

    public static PlanProcessRuleGateOutcome Refuse(string message)
        => new(PlanProcessRuleGateOutcomeKind.Refused, message);

    public static PlanProcessRuleGateOutcome RequiresRefresh(string message)
        => new(PlanProcessRuleGateOutcomeKind.NeedsRefresh, message);

    public bool IsOk => Kind == PlanProcessRuleGateOutcomeKind.Ok;
    public bool IsRefused => Kind == PlanProcessRuleGateOutcomeKind.Refused;
    public bool IsRefreshRequired => Kind == PlanProcessRuleGateOutcomeKind.NeedsRefresh;
}

/// <summary>
/// Classification of a <see cref="PlanProcessRuleGate"/> outcome. <c>Refused</c> is
/// terminal — the plan row moves to Failed with the rule-refusal message.
/// <c>NeedsRefresh</c> is a retryable precondition — the plan row MUST remain Confirmed
/// and the caller returns a top-level busy refusal so the same digest re-applies once the
/// upstream input is available again. The gate itself never produces
/// <c>NeedsRefresh</c>; only the lifecycle does, when the authoritative snapshot required
/// to evaluate a <see cref="BatchOperation"/> cannot be loaded.
/// </summary>
internal enum PlanProcessRuleGateOutcomeKind
{
    Ok = 0,
    Refused = 1,
    NeedsRefresh = 2,
}

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
    /// Returns <see cref="PlanProcessRuleGateOutcome.Ok"/> when <paramref name="batch"/>
    /// may proceed. Returns <see cref="PlanProcessRuleGateOutcome.Refuse(string)"/> when an
    /// enabled <c>makeRequired</c> rule requires a field whose effective value is empty
    /// after <paramref name="source"/> is overlaid by <see cref="BatchOperation.Fields"/>
    /// and the target <c>System.State</c>. <paramref name="source"/> is always an
    /// authoritative point-in-time projection at exactly <see cref="BatchOperation.ExpectedRevision"/>
    /// — see the class remarks — so this method never surfaces a revision-drift
    /// precondition itself.
    /// </summary>
    public async Task<PlanProcessRuleGateOutcome> EvaluateAsync(
        BatchOperation batch,
        WorkItem source,
        CancellationToken ct = default)
    {
        if (_rules is null) return PlanProcessRuleGateOutcome.Ok;

        var typeName = source.Type.Value;
        if (string.IsNullOrEmpty(typeName)) return PlanProcessRuleGateOutcome.Ok;

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
            return PlanProcessRuleGateOutcome.Ok;
        }
        if (rules is null || rules.Count == 0) return PlanProcessRuleGateOutcome.Ok;

        // Cheap short-circuit: with no enabled makeRequired candidate in the rule set,
        // no refusal path exists — skip the pre/post overlay build entirely.
        if (!HasEnabledMakeRequiredCandidate(rules))
            return PlanProcessRuleGateOutcome.Ok;

        // Authoritative-input invariant: PlanLifecycleService only hands us a source
        // projected from an at-revision server snapshot equal to batch.ExpectedRevision
        // (either freshly fetched or carried forward from a prior verified operation on
        // the same work item — AB#721). No cached-projection drift path exists here.

        var fromState = source.State ?? string.Empty;
        var toState = fromState;
        if (TryGetField(batch.Fields, SystemStateField, out var stateOverride)
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
                    return PlanProcessRuleGateOutcome.Refuse(
                        $"Refusing to write work item {source.Id}: an enabled process rule requires "
                        + $"field '{field}' when state is '{toState}', but the effective value is empty.");
                }
            }
        }

        return PlanProcessRuleGateOutcome.Ok;
    }

    /// <summary>
    /// Cheap pre-scan: does the rule set contain at least one enabled rule whose actions
    /// include a <c>makeRequired</c> on a non-blank target field? Only such a candidate
    /// can ever produce a refusal, so a rule set without one skips the overlay work
    /// entirely.
    /// </summary>
    private static bool HasEnabledMakeRequiredCandidate(IReadOnlyList<ProcessRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.IsDisabled) continue;
            foreach (var action in rule.Actions)
            {
                if (!IsMakeRequired(action.ActionType)) continue;
                if (!string.IsNullOrWhiteSpace(action.TargetField))
                    return true;
            }
        }
        return false;
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
            return !Equal(fromState, toState) && Equal(toState, condition.Value);
        if (VerbEquals(verb, "whenValueIsDefined"))
            return !string.IsNullOrEmpty(newValue);
        if (VerbEquals(verb, "whenValueIsNotDefined"))
            return string.IsNullOrEmpty(newValue);

        return false;
    }

    private static bool TryGetField(
        IReadOnlyDictionary<string, string?> fields,
        string referenceName,
        out string? value)
    {
        if (fields.TryGetValue(referenceName, out value)) return true;
        foreach (var field in fields)
        {
            if (!string.Equals(field.Key, referenceName, StringComparison.OrdinalIgnoreCase)) continue;
            value = field.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsMakeRequired(string? actionType)
        => VerbEquals(actionType?.TrimStart('$') ?? string.Empty, "makeRequired");

    private static bool VerbEquals(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Equal(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
