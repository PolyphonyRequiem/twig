namespace Twig.Domain.ValueObjects;

/// <summary>
/// A one-snapshot projection of a single row in the durable <c>pending_changes</c> journal,
/// enriched with the seed remap identity resolved from <c>staged_identities</c> or
/// <c>publish_id_map</c> at read time.
/// <para>
/// This is deliberately a read-only view: it never mutates the journal, never rewrites
/// aliases into published IDs, and never coalesces repeated edits. Consumers see the raw
/// values that were staged, and can rely on the row order — the store returns them ordered
/// by <see cref="PendingChangeId"/>, so a caller can walk the exact sequence in which the
/// user's edits were recorded.
/// </para>
/// <para>
/// The <see cref="Note"/> field is a convenience: it mirrors <see cref="NewValue"/> only for
/// the <c>note</c> and legacy <c>add_note</c> kinds. Every other <see cref="Kind"/> leaves it
/// <see langword="null"/>, including unknown kinds — those are preserved verbatim in
/// <see cref="Kind"/> rather than dropped, so a forward-compatible reader never loses rows.
/// </para>
/// </summary>
public sealed record PendingChangeDetail(
    long PendingChangeId,
    int WorkItemId,
    string Kind,
    string? Field,
    string? Note,
    string? OldValue,
    string? NewValue,
    DateTimeOffset StagedAt,
    SeedRemapIdentity? SeedRemap);

/// <summary>
/// The seed-side identity attached to a pending change whose work item is (or was) a
/// staged seed.
/// <para>
/// Two shapes are possible:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// The row still carries a negative <see cref="PendingChangeDetail.WorkItemId"/> that joins
/// to <c>staged_identities.alias</c>. <see cref="PublishedWorkItemId"/> is
/// <see langword="null"/> — the seed has not been published.
/// </description>
/// </item>
/// <item>
/// <description>
/// The row carries a positive ADO ID that joins to <c>publish_id_map.new_id</c>.
/// <see cref="PublishedWorkItemId"/> is that ADO ID, and <see cref="StagedAlias"/> is the
/// negative number the user saw while staging.
/// </description>
/// </item>
/// </list>
/// <para>
/// A negative work item ID whose alias does not join to <c>staged_identities</c> — a
/// pre-wayfinder-0014 seed left over from an earlier version — is preserved as a row with
/// <see cref="PendingChangeDetail.SeedRemap"/> set to <see langword="null"/>. The projection
/// never guesses an identity that the durable store cannot supply.
/// </para>
/// </summary>
public readonly record struct SeedRemapIdentity(
    StagedIdentity StagedIdentity,
    StagedAlias StagedAlias,
    int? PublishedWorkItemId);
