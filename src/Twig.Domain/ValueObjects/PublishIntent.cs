namespace Twig.Domain.ValueObjects;

/// <summary>
/// The durable record of an *intended* ADO create, written before the call and completed after
/// it (wayfinder 0015, implementing 0001 §4).
/// <para>
/// This is the record 0003 §3 and 0004 §4 both required — there is not a second one. It lives in
/// the durable store (0013) and is keyed on <see cref="StagedIdentity"/> (0014), so neither a
/// cache rebuild nor an alias reissue can detach an intent from the seed that raised it.
/// </para>
/// <para>
/// An intent with no outcome is the reconcilable state: on restart twig can ask ADO
/// <i>"did my create already happen?"</i> by querying for <see cref="IntentTag"/> and then
/// matching locally on title, type and <see cref="RecordedAt"/>.
/// </para>
/// </summary>
public sealed record PublishIntent
{
    /// <summary>
    /// The single constant tag twig stamps on an in-flight create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately constant, and deliberately removed once the publish completes.</b> An
    /// earlier draft stamped a per-create GUID, which mints one NEW project-unique tag per
    /// published item, forever — unbounded growth against ADO's ~5,000 unique-tag project cap,
    /// and single-user bookkeeping written into a namespace every human in the project sees in
    /// their tag picker. 0001 §1 is explicit that the shared substrate is ADO and twig owns only
    /// the pending set, so twig must not colonise a shared namespace to track its own state.
    /// </para>
    /// <para>
    /// One constant tag means the in-use set is bounded by the number of publishes currently
    /// in flight — normally one, since publishing is serial and topologically ordered.
    /// Disambiguation is therefore LOCAL: the recovery query narrows by this tag, then matches
    /// title + type + a creation time at or after the intent was recorded. The time fence stops
    /// an older same-titled item matching.
    /// </para>
    /// <para>
    /// KNOWN LIMITATION: if two seeds of the same type share a title inside one publish window,
    /// the predicate is ambiguous. Publishing is serial and topologically ordered, which makes
    /// that window small, but it does not close it. A per-create key would remove the ambiguity
    /// at the cost of the unbounded shared-tag growth described above.
    /// </para>
    /// <para>
    /// Avoids a leading <c>@</c>, which ADO's query editor would read as a macro and which
    /// makes a tag unqueryable.
    /// </para>
    /// </remarks>
    public const string IntentTag = "twig-publishing";

    /// <summary>The seed this intent was raised for.</summary>
    public required StagedIdentity Identity { get; init; }

    /// <summary>The title the create was issued with — half of the local disambiguation.</summary>
    public required string Title { get; init; }

    /// <summary>The work item type the create was issued with.</summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// When the intent was recorded — always before the ADO call, and therefore a lower bound on
    /// the created item's <c>System.CreatedDate</c>. This is what fences a reused tag: an item
    /// created before this instant cannot be the one this intent produced.
    /// </summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>The ADO id the create produced, or null while the outcome is still unknown.</summary>
    public int? PublishedId { get; init; }

    /// <summary>When the outcome was recorded, or null while the intent is still open.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>True when the ADO call's outcome was never recorded — the reconcilable state.</summary>
    public bool IsOpen => PublishedId is null;
}
