namespace Twig.Domain.ValueObjects;

/// <summary>
/// A minted staged seed identity paired with the display alias registered alongside it.
/// <para>
/// The pairing is deliberate and one-directional: the <see cref="Identity"/> is the key that
/// every durable record uses, and the <see cref="Alias"/> is the negative integer a human or a
/// script types. Nothing joins on the alias.
/// </para>
/// </summary>
public readonly record struct StagedSeedIdentity(StagedIdentity Identity, StagedAlias Alias);
