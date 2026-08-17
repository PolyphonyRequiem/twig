using Twig.Domain.Projections;

namespace Twig.DetailHost;

/// <summary>
/// Sink B (wayfinder 0005 §7, 0006 §6/M4): a second, deliberately different
/// <see cref="IChangeSink"/> owned entirely by the host.
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes this a proof rather than a copy.</b> Twig ships Sink A —
/// <c>Twig.Tui.PendingChangeStoreSink</c>, over SQLite — and 0005 §7 records that a
/// single-implementation abstraction is not a proven seam. This type exists to be the
/// second implementation, and it is deliberately unlike the first in every dimension the
/// seam claims to be free in:
/// </para>
/// <list type="bullet">
/// <item><b>No Twig.Infrastructure, no SQLite, no DI container, no package reference.</b>
/// The whole store is a <see cref="List{T}"/> in this object. If persisting a change ever
/// required more than <c>Twig.Domain</c>, this file would stop compiling.</item>
/// <item><b>A different declared field set</b> — see <see cref="Persistable"/>. Two sinks
/// declaring the same fields prove the interface compiles; they do not prove the seam
/// carries the decision.</item>
/// <item><b>A different outcome profile.</b> Sink A can never report
/// <see cref="Conflicted"/> — it writes to a local staging table nothing races it for. A
/// review queue is shared, so this sink genuinely can, and does.</item>
/// </list>
/// <para>
/// 🔴 <b>The bare <see cref="List{T}"/> below is unguarded on purpose, and must stay that
/// way</b> (AB#353). <see cref="IChangeSink.SubmitAsync"/> states that the host serialises
/// calls, so a sink needs no internal synchronisation; adding a lock here would contradict
/// the contract and tax every third-party implementer reading this sample for guidance. What
/// the contract does <i>not</i> promise is a particular thread — nothing in this type has
/// thread affinity, so that costs it nothing.
/// </para>
/// <para>
/// <b>The modelled host.</b> A duplicate-review pane (0005 §7's named first customer):
/// reviewers record a verdict <i>about the item's content</i> into a queue. Such a host has
/// no authority over workflow or ownership — it cannot move an item's state and cannot
/// reassign it — so it declares none of those fields. That asymmetry is the point: the
/// editable control set a host offers is a consequence of what its destination can hold.
/// </para>
/// </remarks>
internal sealed class ReviewQueueSink : IChangeSink
{
    /// <summary>
    /// The field reference names this review queue can hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Disjoint from Sink A's declaration on purpose</b> (see
    /// <see cref="TwigTuiSinkA.Declaration"/>). Sink A declares the triage fields a work-item
    /// staging table plus <c>twig save</c> can push: <c>System.Title</c>,
    /// <c>System.State</c>, <c>System.AssignedTo</c>. This queue declares the *content and
    /// compliance* fields a reviewer's verdict is made of, and declares no workflow or
    /// identity field at all.
    /// </para>
    /// <para>
    /// A set that merely added one field to Sink A's would satisfy "different" and prove
    /// almost nothing — the interesting failure is a host whose editable surface has a
    /// different <i>shape</i>, not a longer one.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<string> Persistable =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Description",
            "Microsoft.VSTS.Common.AcceptanceCriteria",
            "Microsoft.VSTS.Common.Priority",
            "Contoso.Compliance.ReviewTicket",
        };

    private readonly List<ChangeProposal> _queued = [];
    private readonly int _readRevision;
    private readonly IReadOnlyDictionary<string, string?> _remoteValues;
    private readonly int _remoteRevision;

    /// <summary>
    /// Builds a queue over the revision the host read the item at.
    /// </summary>
    /// <param name="readRevision">The revision the host's snapshot came from.</param>
    /// <param name="remoteRevision">
    /// Where the shared queue's backing item actually is now. When this is ahead of
    /// <paramref name="readRevision"/> the sink reports <see cref="Conflicted"/> rather than
    /// writing — the revision comparison IS the concurrency check, never a value diff.
    /// </param>
    /// <param name="remoteValues">Values the queue can see on the remote item, by field ref.</param>
    internal ReviewQueueSink(
        int readRevision,
        int remoteRevision,
        IReadOnlyDictionary<string, string?> remoteValues)
    {
        _readRevision = readRevision;
        _remoteRevision = remoteRevision;
        _remoteValues = remoteValues;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> PersistableFieldRefs => Persistable;

    /// <summary>Everything this queue has accepted, in order. The host's whole "database".</summary>
    internal IReadOnlyList<ChangeProposal> Queued => _queued;

    /// <inheritdoc />
    public Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default)
    {
        SubmitOutcome outcome;

        if (proposal is FieldEdit edit)
        {
            if (!Persistable.Contains(edit.FieldRef))
            {
                // Refusing a field this sink never declared is the contract's honest failure:
                // visible to the host, rather than a write silently dropped on the floor.
                outcome = new Refused(
                    $"Field '{edit.FieldRef}' is not persistable by the review queue.");
            }
            else if (_remoteRevision > _readRevision)
            {
                _remoteValues.TryGetValue(edit.FieldRef, out var remote);
                outcome = new Conflicted(new EditConflict(
                    _remoteRevision,
                    [new ConflictedField(edit.FieldRef, edit.PriorValue, edit.ProposedValue, remote)]));
            }
            else
            {
                _queued.Add(proposal);

                // 🔴 The revision is returned UNCHANGED, never `_readRevision + 1` (AB#353).
                // Saved.Revision means "the server revision this change was based on". Queueing
                // a verdict writes nothing to the server, so nothing has advanced; minting a
                // revision here would hand the host a number no server ever issued, and would
                // disagree with Sink A (Twig.Tui.PendingChangeStoreSink) about what the field
                // means — an inconsistency between the two reference implementations is exactly
                // what Sink B exists to make impossible.
                outcome = new Saved(_readRevision);
            }
        }
        else if (proposal is StateMove move)
        {
            // A review queue holds verdicts, not workflow. It declares no state field, so it
            // refuses the move outright rather than pretending to route it somewhere.
            outcome = new Refused(
                $"The review queue cannot move '{move.FromState}' → '{move.ToState}'; " +
                "it persists no workflow field.");
        }
        else
        {
            outcome = new Refused("Unrecognised change proposal.");
        }

        return Task.FromResult(outcome);
    }
}

/// <summary>
/// Sink A's declared field set, restated here because it is genuinely unreachable from an
/// external consumer — and a declaration-only sink so a capability can be built over it.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is a MIRROR, not a second real sink.</b> <c>Twig.Tui.PendingChangeStoreSink</c>
/// is <c>internal</c> to an assembly this sample deliberately does not reference — the
/// sample's one <c>ProjectReference</c> is the boundary claim (see the csproj comment). So
/// the only way to check "B differs from A" from outside Twig is to state A's declaration
/// here and compare.
/// </para>
/// <para>
/// The cost is honest and bounded: if Sink A's declaration changes, this constant goes
/// stale. It goes stale <i>loudly</i> — the difference checks in <c>Program</c> are what
/// would start failing, which is the same negative-control discipline the rest of this probe
/// runs on.
/// </para>
/// </remarks>
internal sealed class TwigTuiSinkA : IChangeSink
{
    /// <summary>What <c>Twig.Tui.PendingChangeStoreSink</c> declares as of M3 (AB#183).</summary>
    internal static IReadOnlySet<string> Declaration { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Title", "System.State", "System.AssignedTo",
        };

    /// <inheritdoc />
    public IReadOnlySet<string> PersistableFieldRefs => Declaration;

    /// <inheritdoc />
    /// <remarks>
    /// Never called. This mirror exists to carry a declaration, and submitting through it
    /// would be simulating Twig's staging store rather than proving anything.
    /// </remarks>
    public Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default) =>
        Task.FromResult<SubmitOutcome>(new Refused(
            "This is a declaration-only mirror of Twig's sink, not a destination."));
}
