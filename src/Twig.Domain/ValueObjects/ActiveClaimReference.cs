namespace Twig.Domain.ValueObjects;

/// <summary>
/// The opaque reference AB#736 §4.2.2 fixes: the local claim identifier plus the
/// timestamp at which it was minted into <c>.twig/attachment.json</c>. Both fields
/// are owned end-to-end by AB#739's claim lifecycle — AB#738 observes and
/// preserves them but never mints, rewrites, or interprets either. Carrying the
/// full record (rather than just the id) is what lets a primary-scope write
/// leave the block byte-identical when a claim is already present, satisfying
/// §9.3's "consumers set one field without disturbing the other".
/// </summary>
internal readonly record struct ActiveClaimReference(string ClaimId, DateTimeOffset MintedAt);
