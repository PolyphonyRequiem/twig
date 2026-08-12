namespace Twig.Domain.ValueObjects;

/// <summary>
/// The outcome of asking for one work item type's form layout: the layout, a type whose
/// layout the process will never serve, or no answer at all.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Three states, because there are three facts (AB#247).</b> The fetch previously
/// answered with a nullable <see cref="FormLayout"/>, which can carry only two — and the
/// third had nowhere to go, so a LOCKED system type propagated its raw server error out of
/// the layout command and killed the whole invocation. That is the
/// "nullable-fields-as-state-encoding" anti-pattern named in
/// <c>docs/architecture/result-type-conventions.md</c>, and the remedy the same document
/// prescribes is a Tier 1 discriminated union.
/// </para>
/// <para>
/// 🔴 <b><see cref="Served"/> with no pages is NOT <see cref="Unavailable"/>, and neither
/// is <see cref="Locked"/>.</b> The layout command already distinguished "no layout served"
/// from "a layout with no pages" deliberately — wayfinder-1.0 ticket 1004 carries an open
/// question about whether stock processes serve a layout at all, and collapsing those two
/// would hide the answer. Locked is a THIRD state on top of that distinction, not a
/// re-spelling of either: the process answers, and its answer is "never, for this type".
/// </para>
/// <para>
/// The description surface reaches the same three facts through its own per-type
/// <c>unfetched</c> list. This union is how the layout surface reaches them, so the two
/// verbs can degrade the same way without sharing a document shape.
/// </para>
/// <para>
/// Internal rather than public, deliberately. This type flows only through the internal
/// <c>IFormLayoutProvider</c> seam and its two internal consumers, and the layout shape it wraps
/// is on the record as still under design (wayfinder-1.0 ticket 1004). Making it public would
/// assert stability that nothing has earned yet, for no consumer that exists.
/// </para>
/// </remarks>
internal abstract record FormLayoutResult
{
    private FormLayoutResult() { }

    /// <summary>The process served a layout for this type.</summary>
    /// <param name="Layout">
    /// The layout as served. May legitimately carry no pages — that is the server saying
    /// the form has no tabs, which is a different fact from serving nothing.
    /// </param>
    public sealed record Served(FormLayout Layout) : FormLayoutResult;

    /// <summary>
    /// The type is LOCKED, and the process will never serve its layout.
    /// </summary>
    /// <remarks>
    /// 🔴 A durable fact about the type rather than a transport failure, which is why it is
    /// its own arm rather than an error. Verified live: the locked system types
    /// (<c>TestCase</c>, <c>TestPlan</c>, <c>TestSuite</c>) answer the layout route with
    /// <b>400 VS403115</b>, not 404 — so a caller that only handles "not found" fails hard
    /// on them.
    /// </remarks>
    /// <param name="TypeReferenceName">The type that is locked, for the caller to report.</param>
    public sealed record Locked(string TypeReferenceName) : FormLayoutResult;

    /// <summary>
    /// No layout could be determined — an unknown or disabled type, an undetectable
    /// process, or a server that does not serve a layout for this process.
    /// </summary>
    /// <remarks>
    /// Deliberately weaker than <see cref="Locked"/>: this arm cannot say WHY, and must not
    /// pretend to. Whether stock (non-inherited) processes serve a layout at all is the
    /// open question on ticket 1004, and it is answered by observing this arm, not by
    /// asserting a reason here.
    /// </remarks>
    public sealed record Unavailable : FormLayoutResult;
}
