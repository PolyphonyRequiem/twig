using Twig.Domain.Enums;
using Twig.Domain.Services.Workspace;

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
    /// The DISPLAY LABEL the sprint question is filtered to, or null for the whole team. Carried
    /// so the rule is self-describing rather than depending on ambient configuration at read time.
    /// A display selector matches an item's <c>System.AssignedTo</c> rendering exactly; it is the
    /// right form for an EXPLICIT saved Bench label, not for an inferred-self default (ADO #1106).
    /// </param>
    public static BenchSelector ForCurrentSprint(string? assignedTo)
        => new(SelectorKind.Query,
            assignedTo is null ? CurrentSprintRule : CurrentSprintRule + PayloadSeparator + assignedTo);

    /// <summary>
    /// The sprint rule bound to a CANONICAL principal — the authenticated connection's
    /// <c>uniqueName</c> rather than its display rendering (ADO #1106, Spec #1103).
    /// <para>
    /// 🔴 Required for self defaults: two accounts can share a display name, so a display-keyed
    /// filter would silently merge their items. A canonical selector matches by
    /// <see cref="Aggregates.WorkItem.AssignedToUniqueName"/> when the row carries one; legacy
    /// rows whose canonical column is null fall back to an exact match on their authored
    /// <c>AssignedTo</c> string — a safe narrowing, never widened to display labels.
    /// </para>
    /// </summary>
    /// <param name="uniqueName">
    /// The stable canonical identity (UPN/descriptor) of the self principal. Must be non-empty;
    /// callers resolve this through <see cref="Interfaces.IIterationService.GetAuthenticatedUserIdentityAsync(System.Threading.CancellationToken)"/>
    /// and refuse rather than widen when the connection cannot supply one.
    /// </param>
    public static BenchSelector ForCurrentSprintCanonical(string uniqueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueName);
        // Payload: `current-sprint\u001f\u001f<unique>` — empty display slot keeps legacy
        // single-slot parsing unchanged while adding a second axis beside it.
        return new BenchSelector(
            SelectorKind.Query,
            CurrentSprintRule + PayloadSeparator + PayloadSeparator + uniqueName);
    }

    /// <summary>
    /// The one query rule that exists today: the iteration whose date range covers now, which is
    /// answered from the locally cached iteration list and the local clock — never a network call.
    /// </summary>
    public const string CurrentSprintRule = "current-sprint";

    /// <summary>The named rule this query selector carries. Throws when this is not a query.</summary>
    public string QueryRule
    {
        get
        {
            if (Kind != SelectorKind.Query)
                throw new InvalidOperationException($"Selector of kind {Kind} is not a query selector.");
            var separator = Payload.IndexOf(PayloadSeparator);
            return separator < 0 ? Payload : Payload[..separator];
        }
    }

    /// <summary>
    /// The DISPLAY LABEL this query is filtered to, or null for a canonical-only / unfiltered rule.
    /// </summary>
    public string? QueryAssignedTo => SplitQuery().AssignedTo;

    /// <summary>
    /// The CANONICAL principal this query is filtered to (ADO's <c>uniqueName</c>), or null when
    /// the rule is a legacy display-only selector or carries no filter at all. When set, callers
    /// MUST compare against <see cref="Aggregates.WorkItem.AssignedToUniqueName"/> first, falling
    /// back to an exact match on the authored <c>AssignedTo</c> string ONLY when the row has no
    /// canonical column — never widened to the display label.
    /// </summary>
    public string? QueryAssignedToUniqueName => SplitQuery().AssignedToUniqueName;

    private (string Rule, string? AssignedTo, string? AssignedToUniqueName) SplitQuery()
    {
        if (Kind != SelectorKind.Query)
            throw new InvalidOperationException($"Selector of kind {Kind} is not a query selector.");

        if (QueryRule == BenchQueryRule.Name)
        {
            var configuredRule = BenchQueryRule.Parse(this);
            return (BenchQueryRule.Name, configuredRule.AssignedTo, configuredRule.UniqueName);
        }

        var firstSeparator = Payload.IndexOf(PayloadSeparator);
        if (firstSeparator < 0)
            return (Payload, null, null);

        var rule = Payload[..firstSeparator];
        var rest = Payload[(firstSeparator + 1)..];
        var secondSeparator = rest.IndexOf(PayloadSeparator);
        if (secondSeparator < 0)
        {
            // Legacy single-slot payload: `current-sprint\u001f<display>`.
            return (rule, rest.Length == 0 ? null : rest, null);
        }

        var display = rest[..secondSeparator];
        var canonical = rest[(secondSeparator + 1)..];
        return (
            rule,
            display.Length == 0 ? null : display,
            canonical.Length == 0 ? null : canonical);
    }

    /// <summary>Reads an item or subtree selector's work item id.</summary>
    public int AsWorkItemId()
    {
        if (Kind is not (SelectorKind.Item or SelectorKind.Subtree))
            throw new InvalidOperationException($"Selector of kind {Kind} does not name a work item.");

        return int.Parse(Payload);
    }
}
