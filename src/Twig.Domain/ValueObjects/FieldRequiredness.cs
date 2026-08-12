namespace Twig.Domain.ValueObjects;

/// <summary>
/// What the description says about whether a field is mandatory — in a form that can carry
/// CONDITIONAL requiredness rather than a bare boolean.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>A bare boolean cannot tell the truth here, and the lie is silent.</b> The per-type
/// fields route reports <b>unconditional</b> requiredness only. A field made mandatory by a
/// rule — <i>when State = Done → makeRequired</i> — reads as not-required there. Verified
/// live: <c>Custom.WayfinderAnswer</c> is <c>required: null</c> on the fields route while
/// the rules route carries a <c>makeRequired</c> action for it. A whole-process survey found
/// 59 unconditionally-required fields while every conditionally-required one was invisible.
/// </para>
/// <para>
/// So requiredness is a three-way fact, not a two-way one:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Always"/> — required with no condition attached, from the fields route or from
/// an unconditioned <c>makeRequired</c> rule.
/// </description></item>
/// <item><description>
/// <see cref="Conditional"/> — required only when <see cref="Conditions"/> hold. 🔴 This is
/// the case the obvious implementation renders as simply not-required, which is wrong about
/// exactly the fields a caller most needs.
/// </description></item>
/// <item><description>
/// <see cref="Never"/> — no source makes it mandatory.
/// </description></item>
/// </list>
/// <para>
/// 🔴 <b><see cref="Conditions"/> is an ordered list sorted by the assembler on an ORDINAL
/// key, and that is load-bearing.</b> Rules arrive from the server in server order. Carrying
/// them into the document unsorted would break byte-stability — the single most important
/// property of this feature — in a way no unit test on one run would catch.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Implementation Decision 5(a), Solution S2.
/// </para>
/// </remarks>
/// <param name="Kind">Which of the three cases this is.</param>
/// <param name="Conditions">
/// The distinct conditions under which the field is required, sorted ordinal. Empty unless
/// <see cref="Kind"/> is <see cref="FieldRequirednessKind.Conditional"/>.
/// <para>
/// The conditions are alternatives: the field is required when ANY ONE of them holds. Within
/// a single condition every clause must hold, because a rule's conditions are conjunctive on
/// the server.
/// </para>
/// </param>
internal sealed record FieldRequiredness(
    FieldRequirednessKind Kind,
    IReadOnlyList<FieldRequirednessCondition> Conditions)
{
    /// <summary>Required with no condition attached.</summary>
    internal static readonly FieldRequiredness Always =
        new(FieldRequirednessKind.Always, []);

    /// <summary>Nothing makes this field mandatory.</summary>
    internal static readonly FieldRequiredness Never =
        new(FieldRequirednessKind.Never, []);

    /// <summary>
    /// Required only under the given conditions.
    /// </summary>
    /// <remarks>
    /// 🔴 Falls back to <see cref="Never"/> on an EMPTY condition list rather than producing a
    /// "conditional" requiredness with no condition. A conditional-with-no-condition would
    /// render as a warning the reader cannot act on — it names no state, no field, and no
    /// value — which is a different flavour of the same dishonesty this type exists to
    /// prevent. An unconditioned <c>makeRequired</c> is <see cref="Always"/>, and the caller
    /// classifies it that way before reaching here.
    /// </remarks>
    internal static FieldRequiredness Conditionally(
        IReadOnlyList<FieldRequirednessCondition> conditions)
        => conditions.Count == 0
            ? Never
            : new FieldRequiredness(FieldRequirednessKind.Conditional, conditions);
}

/// <summary>The three ways a field can stand with respect to being mandatory.</summary>
/// <remarks>
/// 🔴 Deliberately three values and not a nullable boolean. A nullable boolean would let a
/// consumer write <c>required == true</c> and silently treat the conditional case as
/// not-required, which is the exact defect AB#236 exists to remove.
/// </remarks>
internal enum FieldRequirednessKind
{
    /// <summary>No source makes this field mandatory.</summary>
    Never = 0,

    /// <summary>Required only when one of the carried conditions holds.</summary>
    Conditional = 1,

    /// <summary>Required with no condition attached.</summary>
    Always = 2,
}

/// <summary>
/// One condition under which a field becomes required — the condition set of a single
/// <c>makeRequired</c> rule.
/// </summary>
/// <remarks>
/// Every clause must hold for the condition to fire: a rule's conditions are conjunctive on
/// the server. Two conditions on the same field are alternatives.
/// </remarks>
/// <param name="Clauses">
/// The clauses, sorted ordinal by the assembler. Never empty — an unconditioned
/// <c>makeRequired</c> is unconditional requiredness, not a condition with no clauses.
/// </param>
internal sealed record FieldRequirednessCondition(
    IReadOnlyList<FieldRequirednessClause> Clauses);

/// <summary>One clause of a requiredness condition, carried verbatim from the rules route.</summary>
/// <remarks>
/// Twig does not reinterpret the server's vocabulary. <c>when</c>, <c>whenNot</c>,
/// <c>whenWas</c>, <c>whenChanged</c>, <c>whenValueIsDefined</c> and the rest are passed
/// through as the server spells them, minus the leading <c>$</c> some routes prefix, so a
/// reader diffing two documents is comparing the server's own words rather than Twig's
/// paraphrase of them.
/// </remarks>
/// <param name="ConditionType">The condition verb, e.g. <c>when</c>.</param>
/// <param name="Field">The field the condition tests, e.g. <c>System.State</c>.</param>
/// <param name="Value">The value tested against, or <c>null</c> where the verb takes none.</param>
internal sealed record FieldRequirednessClause(
    string ConditionType,
    string Field,
    string? Value);
