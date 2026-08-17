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
    /// <para>
    /// Declaring a field here is a promise to persist it. A sink that lists a field it silently
    /// drops reintroduces the exact silent-loss failure this design exists to prevent.
    /// </para>
    /// <para>
    /// 🔴 <b>The set MUST use a case-insensitive comparer</b> —
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. Azure DevOps field reference names are
    /// case-insensitive on the wire, so a sink returning a default-comparer
    /// <see cref="HashSet{T}"/> makes <see cref="EditCapability.CanEdit"/> answer <c>false</c>
    /// for <c>system.title</c> while answering <c>true</c> for <c>System.Title</c> — the same
    /// silent-loss failure, arrived at by spelling rather than by omission. A sink that ignores
    /// this is a defect <i>in that sink</i>.
    /// </para>
    /// <para>
    /// This is stated rather than enforced on purpose. <see cref="EditCapability"/> deliberately
    /// does not snapshot or re-wrap the set, because copying it at construction would silently
    /// settle a separate question nobody has ruled on — whether a sink may grow its persistable
    /// set at runtime — and that question is left open rather than closed by side effect.
    /// </para>
    /// </remarks>
    IReadOnlySet<string> PersistableFieldRefs { get; }

    /// <summary>
    /// Persists <paramref name="proposal"/>, or reports why it could not be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sink that talks to a server is expected to retry once against the refreshed revision
    /// before reporting <see cref="Conflicted"/> — a single collision is usually a stale read,
    /// not genuine contention. Reporting the collision without attempting the retry forces
    /// every host to implement conflict resolution before it can save anything at all.
    /// </para>
    /// <para>
    /// 🔴 <b>Calls are serialised by the host; implementations may assume no two
    /// <see cref="SubmitAsync"/> calls overlap and need no internal synchronisation.</b>
    /// Implementations MUST NOT assume a particular thread — continuations may resume on the
    /// thread pool. Serialisation is what makes an unguarded field or collection inside a sink
    /// correct rather than merely lucky; the thread caveat is why anything with thread affinity
    /// (a UI control, a thread-static, a non-reentrant native handle) must still be marshalled
    /// by the sink itself.
    /// </para>
    /// <para>
    /// <b><see cref="Saved.Revision"/> is the server revision the change was BASED ON</b>, not a
    /// new revision the sink has minted. A sink is in no position to know what revision the
    /// server will assign — and for a staging or queueing sink no server write has happened at
    /// all — so it reports where the item still is and leaves the advance to whatever eventually
    /// pushes.
    /// </para>
    /// </remarks>
    Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default);
}
