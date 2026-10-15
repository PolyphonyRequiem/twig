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
/// <i>"did my create already happen?"</i> using the shared <see cref="IntentTag"/> for
/// discovery and the staged GUID in the immutable initial Description for attribution.
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
    /// One shared tag bounds the project vocabulary independently of the number of seeds.
    /// The exact staged GUID is written into Description in the same create request and
    /// read from revision one, so current title, area or Description edits cannot reattribute
    /// the request. Missing discovery evidence never authorizes replay of an unknown create.
    /// </para>
    /// <para>
    /// Description cleanup follows durable ID, intent, map and native outcome recording,
    /// uses a fresh revision fence, and preserves current human edits. Cleanup removes the
    /// current marker only; the staged GUID remains in ADO revision history.
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
