namespace Twig.Domain.ValueObjects;

/// <summary>
/// One rule on a work item type: the conditions under which it fires and what it then does.
/// </summary>
/// <remarks>
/// <para>
/// Shared between the shipped <c>twig process rules</c> path (which reads rules for their
/// <c>makeRequired</c> actions) and the process description, which carries rules whole.
/// </para>
/// <para>
/// 🔴 <b><see cref="Customization"/> and <see cref="Name"/> are optional with defaults on
/// purpose.</b> They were added by AB#238 for the description, and defaulting them keeps every
/// existing construction site compiling unchanged. The default for
/// <see cref="Customization"/> is <see cref="RuleCustomization.Unknown"/> and NOT a real class:
/// a caller that does not supply the tag has not learnt one, and inventing
/// <c>system</c> there would let the reader's own filter throw away authored rules.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Solution S3, Implementation Decision 4.
/// </para>
/// </remarks>
/// <param name="Conditions">The clauses, conjunctive, that gate the rule.</param>
/// <param name="Actions">What the rule does when it fires.</param>
/// <param name="IsDisabled">
/// Whether the process has disabled the rule. A disabled rule does not fire — but it IS
/// carried into the description, because a rule disabled on one process and enabled on
/// another is a real difference and dropping it would diff clean.
/// </param>
/// <param name="Customization">
/// 🔴 Whether the rule was authored here, inherited, or is system plumbing — the tag that
/// makes the carry-everything ruling bearable, because it is the reader's filter for the ~54
/// inherited rules a derived type carries.
/// </param>
/// <param name="Name">
/// The rule's display name where the server gives one, else <c>null</c>. Authored rules
/// carry a human name (<i>"Epic must state what it delivered"</i>); system plumbing rules
/// carry <c>null</c>, verified live. Never used as identity — it is not unique and is
/// commonly absent.
/// </param>
internal sealed record ProcessRule(
    IReadOnlyList<RuleCondition> Conditions,
    IReadOnlyList<RuleAction> Actions,
    bool IsDisabled,
    RuleCustomization? Customization = null,
    string? Name = null)
{
    /// <summary>
    /// The rule's customization tag, normalised so the STORED value is never <c>null</c>.
    /// </summary>
    /// <remarks>
    /// 🔴 Normalised in the initialiser rather than only at the read site, because this is a
    /// record and its compiler-generated <c>Equals</c> compares the stored field. Left
    /// un-normalised, a rule constructed without the tag and one constructed with
    /// <see cref="RuleCustomization.Unknown"/> are semantically identical and compare
    /// UNEQUAL — a trap in a codebase that leans on record equality and on <c>HashSet</c>, and
    /// invisible until someone writes <c>ShouldBe</c> over a rule list.
    /// <para>
    /// The parameter stays nullable so every existing construction site keeps compiling; the
    /// default converges with "the server did not say" on the one honest answer.
    /// </para>
    /// </remarks>
    internal RuleCustomization? Customization { get; init; } =
        Customization ?? RuleCustomization.Unknown;

    /// <summary>The customization tag, never <c>null</c>.</summary>
    internal RuleCustomization CustomizationOrUnknown => Customization ?? RuleCustomization.Unknown;
}

internal sealed record RuleCondition(string ConditionType, string Field, string? Value);

internal sealed record RuleAction(string ActionType, string TargetField, string? Value);
