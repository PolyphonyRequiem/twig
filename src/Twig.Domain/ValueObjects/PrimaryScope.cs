namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable value carrying the identity a managed worktree is attached to.
/// The URL is stored so a stolen or moved <c>.twig/</c> can be recognized against
/// the wrong connection at read time before the system store answers
/// (§4.2.2 of the worktree attachment storage design, AB#736).
/// <para>
/// <see cref="WorkItemId"/> is the sole identity. The URL is advisory provenance;
/// the title is not persisted here — status projections resolve it on read.
/// </para>
/// </summary>
internal readonly record struct PrimaryScope(
    int WorkItemId,
    string WorkItemUrl,
    DateTimeOffset AttachedAt);
