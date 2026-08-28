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
/// <para>
/// 🔴 <b>A requirement the same rule set also supplies is not a precondition (AB#803).</b>
/// ADO declares <c>makeRequired</c> and the action that satisfies it as SEPARATE rules on
/// the SAME condition — verified live on Hyperbright's <c>Task</c>, where
/// <i>State = Doing AND was To Do</i> carries <c>makeRequired System.Reason</c> beside
/// <c>copyValue System.Reason = Started</c>, and <c>makeRequired ActivatedBy</c> beside
/// <c>copyFromCurrentUser ActivatedBy</c>. Reading only the requiredness half forces the
/// caller to stage values the server generates for itself, and refused every honest Task
/// state transition. The two halves are enforced by ONE engine under ONE condition, so
/// they cannot come apart: if the engine runs, it writes the supplied value before it
/// checks requiredness; if the engine is bypassed, it checks no requiredness either.
/// A field a firing rule supplies is therefore never empty at the moment requiredness is
/// evaluated, and refusing on it is a false positive. Requirements with no paired
/// supplier — the authored close gates AB#673 exists to defend — still refuse.
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
    /// and the target <c>System.State</c>, AND no rule firing on the same batch supplies
    /// that field a value of its own (AB#803 — see the class remarks).
    /// <paramref name="source"/> is always an authoritative point-in-time projection at
    /// exactly <see cref="BatchOperation.ExpectedRevision"/> — see the class remarks — so
    /// this method never surfaces a revision-drift precondition itself.
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

        // Pass 1 (hot): every field a firing rule makes required whose effective value is
        // empty after the overlay. This is only a CANDIDATE list — a paired supplier may
        // still satisfy it. Nothing is allocated unless there is something to answer for,
        // which is the ordinary case for a batch that stages what the process asks for.
        List<string>? candidates = null;
        foreach (var rule in rules)
        {
            if (!Fires(rule, fromState, toState, oldFields, newFields)) continue;

            foreach (var action in rule.Actions)
            {
                if (!IsMakeRequired(action.ActionType)) continue;
                var field = action.TargetField;
                if (string.IsNullOrWhiteSpace(field)) continue;
                newFields.TryGetValue(field, out var value);
                if (!string.IsNullOrEmpty(value)) continue;
                (candidates ??= []).Add(field);
            }
        }

        if (candidates is null) return PlanProcessRuleGateOutcome.Ok;

        // Pass 2 (cold, AB#803): drop every candidate the firing rule set supplies a value
        // for. Reached only when pass 1 found a candidate, and it drains that same list
        // rather than building a second collection beside it.
        DropSuppliedFields(candidates, rules, fromState, toState, oldFields, newFields);

        // What survives is required, empty, and unsupplied. Report the first in rule order.
        if (candidates.Count > 0)
        {
            return PlanProcessRuleGateOutcome.Refuse(
                $"Refusing to write work item {source.Id}: an enabled process rule requires "
                + $"field '{candidates[0]}' when state is '{toState}', but the effective value is empty.");
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

    /// <summary>
    /// Whether <paramref name="rule"/> runs on this batch. The ONE definition of "firing",
    /// shared by the requirement pass and the supplier pass so the two cannot drift.
    /// </summary>
    private static bool Fires(
        ProcessRule rule,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> oldFields,
        IReadOnlyDictionary<string, string?> newFields)
        => !rule.IsDisabled
            && ConditionsFire(rule.Conditions, fromState, toState, oldFields, newFields);

    /// <summary>
    /// Removes from <paramref name="candidates"/> every field the firing rule set will
    /// populate itself (AB#803).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Membership of the firing set is the predicate, NOT condition equality.</b> ADO
    /// declares the requirement and its supplier as separate rules, and their condition
    /// sets are related but not always identical — live <c>Task</c> pairs
    /// <i>when State = Done → makeRequired ClosedDate</i> with a supplier on that same bare
    /// condition, while <c>ClosedBy</c>'s pair both carry the narrower
    /// <i>… AND whenWas State = Doing</i>. Matching condition sets textually would refuse a
    /// batch whose narrower supplier genuinely fires, and would still be no safer: every
    /// rule that fires runs, so what decides whether the field ends up populated is
    /// whether the supplier fires — not how its condition happens to be written. A
    /// supplier that does NOT fire is already excluded by <see cref="Fires"/>.
    /// </para>
    /// <para>
    /// Removal preserves the order of what remains, so the refusal still names the first
    /// unsatisfied field in rule order. Called only once a candidate exists, so the second
    /// walk over the rule set is a refusal-path cost, never an ordinary-write one.
    /// </para>
    /// </remarks>
    private static void DropSuppliedFields(
        List<string> candidates,
        IReadOnlyList<ProcessRule> rules,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, string?> oldFields,
        IReadOnlyDictionary<string, string?> newFields)
    {
        foreach (var rule in rules)
        {
            if (candidates.Count == 0) return;
            if (!Fires(rule, fromState, toState, oldFields, newFields)) continue;

            foreach (var action in rule.Actions)
            {
                if (string.IsNullOrWhiteSpace(action.TargetField)) continue;
                if (!Supplies(action, newFields)) continue;

                for (var i = candidates.Count - 1; i >= 0; i--)
                {
                    if (VerbEquals(candidates[i], action.TargetField))
                        candidates.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="action"/> gives its target field a NON-EMPTY value when the
    /// rule fires. An action that supplies nothing, or supplies something that is itself
    /// empty, leaves the requirement unsatisfied and must not clear the refusal.
    /// </summary>
    /// <remarks>
    /// A field-sourced supplier is resolved by looking its <c>Value</c> up as a field
    /// reference name in the post-overlay view. The rules payload does not always carry a
    /// reference name there — the API's own sample shows a numeric field id — so a Value
    /// that resolves to nothing supplies nothing and the requirement stands. That is the
    /// fail-closed direction: at worst the caller stages a field it need not have.
    /// </remarks>
    private static bool Supplies(RuleAction action, IReadOnlyDictionary<string, string?> newFields)
        => ClassifySupply(action.ActionType) switch
        {
            RuleActionSupply.Generated => true,
            RuleActionSupply.Literal => !string.IsNullOrEmpty(action.Value),
            RuleActionSupply.Field => !string.IsNullOrWhiteSpace(action.Value)
                && newFields.TryGetValue(action.Value, out var sourceValue)
                && !string.IsNullOrEmpty(sourceValue),
            _ => false,
        };

    /// <summary>
    /// Where a rule action's value comes from, which is what decides whether it can be
    /// trusted to be non-empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This is the whole closed action vocabulary, partitioned — not a guess.</b> The
    /// Rules API documents <c>actionType</c> as fourteen values: <c>makeRequired</c>,
    /// <c>makeReadOnly</c>, <c>setDefaultValue</c>, <c>setDefaultFromClock</c>,
    /// <c>setDefaultFromField</c>, <c>copyValue</c>, <c>copyFromClock</c>,
    /// <c>copyFromCurrentUser</c>, <c>copyFromField</c>, <c>setValueToEmpty</c>,
    /// <c>copyFromServerClock</c>, <c>copyFromServerCurrentUser</c>,
    /// <c>hideTargetField</c> and <c>disallowValue</c>; Hyperbright additionally emits
    /// <c>setDefaultFromCurrentUser</c>, observed live. Every one of those is classified
    /// below or falls to <see cref="RuleActionSupply.None"/>, so the partition is complete
    /// rather than open-ended — the alternative, recognising only the verbs one process
    /// happens to use today, reinstates this defect for the next process that uses another.
    /// </para>
    /// <para>
    /// 🔴 <b>Fails closed on an unknown verb.</b> The five non-suppliers above either
    /// restrict a field (<c>makeReadOnly</c>, <c>disallowValue</c>, <c>hideTargetField</c>)
    /// or clear it (<c>setValueToEmpty</c>), and a verb this evaluator does not recognise
    /// supplies nothing either — so the requirement stands and the batch is refused.
    /// Treating an unknown verb as a supplier would let a genuinely empty gate field
    /// through, which is the exact failure AB#673 built this gate to stop.
    /// </para>
    /// <para>
    /// The leading <c>$</c> sigil is trimmed because the rules payload is inconsistent
    /// about it across api-versions and customization types, the same way
    /// <see cref="IsMakeRequired"/> and <c>DependentFieldReconciler</c> trim it.
    /// </para>
    /// </remarks>
    private static RuleActionSupply ClassifySupply(string? actionType)
    {
        var verb = actionType?.TrimStart('$') ?? string.Empty;

        // Value carried on the action itself.
        if (VerbEquals(verb, "copyValue") || VerbEquals(verb, "setDefaultValue"))
            return RuleActionSupply.Literal;

        // Value copied from another field on the same item; the action's Value names it.
        if (VerbEquals(verb, "copyFromField") || VerbEquals(verb, "setDefaultFromField"))
            return RuleActionSupply.Field;

        // Value generated by the server — the authenticated identity or the clock. Never
        // empty, and not knowable to a client ahead of the write.
        if (VerbEquals(verb, "copyFromCurrentUser")
            || VerbEquals(verb, "copyFromServerCurrentUser")
            || VerbEquals(verb, "setDefaultFromCurrentUser")
            || VerbEquals(verb, "copyFromClock")
            || VerbEquals(verb, "copyFromServerClock")
            || VerbEquals(verb, "setDefaultFromClock"))
        {
            return RuleActionSupply.Generated;
        }

        return RuleActionSupply.None;
    }

    /// <summary>Where a firing rule action's value comes from — see <see cref="ClassifySupply"/>.</summary>
    private enum RuleActionSupply
    {
        /// <summary>The action supplies no value; it cannot satisfy a requirement.</summary>
        None = 0,

        /// <summary>The server generates the value (identity or clock); never empty.</summary>
        Generated = 1,

        /// <summary>The action carries the literal value on itself.</summary>
        Literal = 2,

        /// <summary>The value is copied from the field the action names.</summary>
        Field = 3,
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
