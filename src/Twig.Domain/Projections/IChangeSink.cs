namespace Twig.Domain.Projections;

/// <summary>
/// The destination a host supplies for changes it wants persisted, and — by declaring what it
/// can store — the authority on what may be edited at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Twig ships one implementation and it is not privileged.</b> A host is free to send
/// changes to a review queue, an in-memory list, or anywhere else. Read-only hosts never
/// implement this and never acquire an <see cref="EditCapability"/>.
/// </para>
/// <para>
/// 🔴 <b><see cref="PersistableFieldRefs"/> is what makes controls editable — NOT
/// <see cref="DetailControl.ReadOnly"/>,</b> which stays reported-but-never-enforced. Wiring
/// editability to the server's read-only flag looks safer and is the opposite: Azure DevOps
/// marks almost no field read-only, so the flag-as-authority would make nearly the whole form
/// typable while the sink can persist a handful of fields. The user types, saves, and the edit
/// is silently discarded because the sink had nowhere to put it. Sink-declared mutability
/// cannot produce that failure by construction, because the editable set <i>is</i> the
/// persistable set. The inverse failure — a field locked that the server would have accepted —
/// is visible, honest, and fixed by teaching the sink one more field.
/// </para>
/// </remarks>
public interface IChangeSink
{
    /// <summary>
    /// The field reference names this sink can store. Exactly these fields accept input.
    /// </summary>
    /// <remarks>
    /// Declaring a field here is a promise to persist it. A sink that lists a field it silently
    /// drops reintroduces the exact silent-loss failure this design exists to prevent.
    /// </remarks>
    IReadOnlySet<string> PersistableFieldRefs { get; }

    /// <summary>
    /// Persists <paramref name="proposal"/>, or reports why it could not be.
    /// </summary>
    /// <remarks>
    /// A sink that talks to a server is expected to retry once against the refreshed revision
    /// before reporting <see cref="Conflicted"/> — a single collision is usually a stale read,
    /// not genuine contention. Reporting the collision without attempting the retry forces
    /// every host to implement conflict resolution before it can save anything at all.
    /// </remarks>
    Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default);
}
