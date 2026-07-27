namespace Twig.Domain.ValueObjects;

/// <summary>
/// One row of the publish map: the durable identity a seed was staged under, the display alias
/// it wore, and the ADO ID it published as (wayfinder 0014).
/// <para>
/// <see cref="Identity"/> is the key. <see cref="Alias"/> is carried only so callers that start
/// from a number a user typed can render and match it, and may be <see langword="null"/> for a
/// legacy row whose alias predates the register.
/// </para>
/// </summary>
public readonly record struct PublishMapping(StagedIdentity Identity, StagedAlias? Alias, int NewId);
