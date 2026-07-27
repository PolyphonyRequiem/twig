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
/// <i>"did my create already happen?"</i> by querying for <see cref="IdempotencyTag"/>. ADO
/// publishes no idempotency key for creates, so twig stamps its own — see
/// <see cref="PublishIntent.TagFor"/>.
/// </para>
/// </summary>
public sealed record PublishIntent
{
    /// <summary>The prefix every twig-stamped idempotency tag carries.</summary>
    /// <remarks>
    /// A tag, not a custom field: tags are per-work-item data that any Contributor may create,
    /// so stamping one needs no change to the organisation's process template. Deliberately
    /// avoids a leading <c>@</c>, which ADO's query editor would read as a macro and which makes
    /// a tag unqueryable.
    /// </remarks>
    public const string TagPrefix = "twig-intent:";

    /// <summary>The seed this intent was raised for.</summary>
    public required StagedIdentity Identity { get; init; }

    /// <summary>The tag stamped on the ADO item, and the key the recovery query searches for.</summary>
    public required string IdempotencyTag { get; init; }

    /// <summary>When the intent was recorded — always before the ADO call.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>The ADO id the create produced, or null while the outcome is still unknown.</summary>
    public int? PublishedId { get; init; }

    /// <summary>When the outcome was recorded, or null while the intent is still open.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>True when the ADO call's outcome was never recorded — the reconcilable state.</summary>
    public bool IsOpen => PublishedId is null;

    /// <summary>
    /// The idempotency tag for a given identity. Deterministic, so the recovery query can be
    /// rebuilt from the durable record alone without having stored the tag.
    /// </summary>
    public static string TagFor(StagedIdentity identity) => TagPrefix + identity;
}
