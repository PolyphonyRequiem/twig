using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// One rule on a Bench: <i>is this item on this Bench?</i> (docs/specs/bench.spec.md §2).
/// <para>
/// A Bench stores the RULE, never the results — what a selector matches is recomputed on every
/// look, so a Bench keeps up with reality rather than going stale.
/// </para>
/// <para>
/// 🔴 A selector carries no position. Membership is the UNION of a Bench's selectors and order
/// does not matter; two Benches holding the same selectors show the same items. An ordinal here
/// would invite sequential evaluation, which passes every other test while making construction
/// order observable.
/// </para>
/// </summary>
/// <param name="Kind">Which question this selector asks.</param>
/// <param name="Payload">
/// The kind's settings, opaque to storage. Spec §2 requires further kinds without a schema
/// change, so this is a per-kind string rather than a column per kind.
/// </param>
public sealed record BenchSelector(SelectorKind Kind, string Payload)
{
    /// <summary>
    /// Separates a query rule's name from its settings. A unit separator is used rather than JSON
    /// because this assembly is trim- and AOT-clean: reflection-based serialisation would need a
    /// source-generated context for one two-field record, and cannot appear in a payload since
    /// it is not typeable in an ADO display name.
    /// </summary>
    private const char PayloadSeparator = '\u001f';

    /// <summary>A pin: matches the one item with this id.</summary>
    public static BenchSelector ForItem(int workItemId)
        => new(SelectorKind.Item, workItemId.ToString());

    /// <summary>A tree pin: matches this item and its descendants as they are now.</summary>
    public static BenchSelector ForSubtree(int rootWorkItemId)
        => new(SelectorKind.Subtree, rootWorkItemId.ToString());

    /// <summary>
    /// The sprint rule — today's hard-coded question, expressed as an ordinary query selector.
    /// <para>
    /// 🔴 This is the FIRST ROW of the selector mechanism, not a special case beside it. If the
    /// sprint question stayed as branching logic, the default Bench would not be a Bench and the
    /// parity bar would be met by a fiction (spec §3).
    /// </para>
    /// <para>
    /// A rule is one named kind plus its settings — deliberately NOT a query language. What a
    /// query selector can express beyond today's question is out of scope (spec, Out of Scope);
    /// a further kind is added BESIDE this one rather than expressed within it.
    /// </para>
    /// </summary>
    /// <param name="assignedTo">
    /// The person the sprint question is filtered to, or null for the whole team. Stored so the
    /// rule is self-describing rather than depending on ambient configuration at read time.
    /// </param>
    public static BenchSelector ForCurrentSprint(string? assignedTo)
        => new(SelectorKind.Query,
            assignedTo is null ? CurrentSprintRule : CurrentSprintRule + PayloadSeparator + assignedTo);

    /// <summary>
    /// The one query rule that exists today: the iteration whose date range covers now, which is
    /// answered from the locally cached iteration list and the local clock — never a network call.
    /// </summary>
    public const string CurrentSprintRule = "current-sprint";

    /// <summary>The named rule this query selector carries. Throws when this is not a query.</summary>
    public string QueryRule => SplitQuery().Rule;

    /// <summary>The person this query is filtered to, or null for the whole team.</summary>
    public string? QueryAssignedTo => SplitQuery().AssignedTo;

    private (string Rule, string? AssignedTo) SplitQuery()
    {
        if (Kind != SelectorKind.Query)
            throw new InvalidOperationException($"Selector of kind {Kind} is not a query selector.");

        var index = Payload.IndexOf(PayloadSeparator);
        return index < 0
            ? (Payload, null)
            : (Payload[..index], Payload[(index + 1)..]);
    }

    /// <summary>Reads an item or subtree selector's work item id.</summary>
    public int AsWorkItemId()
    {
        if (Kind is not (SelectorKind.Item or SelectorKind.Subtree))
            throw new InvalidOperationException($"Selector of kind {Kind} does not name a work item.");

        return int.Parse(Payload);
    }
}
