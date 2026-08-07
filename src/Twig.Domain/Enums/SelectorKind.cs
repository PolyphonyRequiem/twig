namespace Twig.Domain.Enums;

/// <summary>
/// The kinds of selector a Bench can hold (docs/specs/bench.spec.md §2).
/// <para>
/// A selector answers one question: <i>is this item on this Bench?</i> A pin is not a different
/// kind of thing from a query — a pin matches one item, a query matches a body of work. They
/// differ in how many items they match, not in what they are.
/// </para>
/// <para>
/// 🔴 The seeds-and-unpushed guard is deliberately NOT a kind here. It is an invariant on
/// evaluation, not a selector: a person must not be able to remove it by editing a Bench.
/// </para>
/// </summary>
public enum SelectorKind
{
    /// <summary>Matches exactly one work item. This is what a pin becomes.</summary>
    Item = 0,

    /// <summary>
    /// Matches one work item and its descendants <b>as they are now</b>. This is what a tree pin
    /// becomes, and it is why "a pin is just an id" is too weak a model — a subtree selector
    /// matches children created after it was added.
    /// </summary>
    Subtree = 1,

    /// <summary>
    /// Matches a body of work. Carries an ADO query as a <b>refresh rule</b> — the rule is how
    /// matching items reach the local cache, and is never what runs when somebody looks at their
    /// Bench (spec, Solution).
    /// </summary>
    Query = 2,
}
