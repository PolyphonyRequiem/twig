using Twig.Domain.Interfaces;
using Twig.Domain.Projections;

namespace Twig.Tui;

/// <summary>
/// The <see cref="IChangeSink"/> Twig ships for its own TUI: changes are staged into the
/// local SQLite <see cref="IPendingChangeStore"/> and pushed later by <c>twig save</c>.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This sink is not privileged</b> (wayfinder 0005 §7). It is one implementation of a
/// host-supplied seam, and a host that wants its changes somewhere else — a review queue, an
/// in-memory list — implements <see cref="IChangeSink"/> instead and never links
/// <c>Twig.Infrastructure</c>. What makes the seam real is that a second implementation
/// exists; a single-implementation abstraction is not a proven seam.
/// </para>
/// <para>
/// <b>Why a sink is bound to one work item.</b> <see cref="ChangeProposal"/> deliberately
/// carries no work-item id — it says what changed, not what it changed on — so the binding
/// lives here. An unbound sink still <i>declares</i> what it can persist (the declaration does
/// not vary by item, which is why <see cref="WorkItemFormView"/> can derive editability before
/// anything is loaded) but refuses submissions, because staging a change against no item would
/// be a silent no-op and silent loss is the failure this whole design exists to prevent.
/// </para>
/// <para>
/// <b>This sink never reports <see cref="Conflicted"/>.</b> It writes to a local staging table
/// that nothing else races it for; a revision collision is discovered later, when <c>twig
/// save</c> pushes the staged rows to the server. Reporting a conflict here would be inventing
/// contention this sink cannot observe.
/// </para>
/// </remarks>
internal sealed class PendingChangeStoreSink : IChangeSink
{
    /// <summary>
    /// The fields the staging store plus <c>twig save</c> can carry end to end today.
    /// </summary>
    /// <remarks>
    /// The SQLite table itself accepts any field reference name, so this set is bounded by
    /// what the flush path can actually push, not by the schema. Widening it means teaching
    /// the flush path one more field — which is the honest, visible failure mode 0005 §1
    /// chose over the silent one.
    /// </remarks>
    private static readonly IReadOnlySet<string> Persistable =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Title", "System.State", "System.AssignedTo",
        };

    private readonly IPendingChangeStore _store;
    private readonly int? _workItemId;
    private readonly int _revision;

    /// <summary>Builds a sink over <paramref name="store"/> that is not yet bound to an item.</summary>
    public PendingChangeStoreSink(IPendingChangeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    private PendingChangeStoreSink(IPendingChangeStore store, int workItemId, int revision)
    {
        _store = store;
        _workItemId = workItemId;
        _revision = revision;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> PersistableFieldRefs => Persistable;

    /// <summary>
    /// Returns a sink that stages against <paramref name="workItemId"/>.
    /// </summary>
    /// <param name="revision">
    /// The revision the item was read at. Staging does not advance it — the returned
    /// <see cref="Saved.Revision"/> reports where the item still is, not a new server
    /// revision this sink is in no position to mint.
    /// </param>
    public PendingChangeStoreSink BoundTo(int workItemId, int revision) =>
        new(_store, workItemId, revision);

    /// <inheritdoc />
    public async Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default)
    {
        if (_workItemId is not { } id)
            return new Refused("This sink is not bound to a work item; nothing would be staged.");

        var rows = new List<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)>();

        if (proposal is FieldEdit edit)
        {
            if (!Persistable.Contains(edit.FieldRef))
                return new Refused($"Field '{edit.FieldRef}' is not persistable by the pending-change store.");

            rows.Add((ChangeTypeFor(edit.FieldRef), edit.FieldRef, edit.PriorValue, edit.ProposedValue));
        }
        else if (proposal is StateMove move)
        {
            foreach (var accompanying in move.Accompanying)
            {
                if (!Persistable.Contains(accompanying.FieldRef))
                {
                    return new Refused(
                        $"Field '{accompanying.FieldRef}' accompanying the state move is not persistable " +
                        "by the pending-change store.");
                }
            }

            // One unit of work, one transaction: the move and everything riding on it land
            // together or not at all, so a retry after a partial failure cannot duplicate rows.
            rows.Add(("state", "System.State", move.FromState, move.ToState));
            foreach (var accompanying in move.Accompanying)
            {
                rows.Add((ChangeTypeFor(accompanying.FieldRef), accompanying.FieldRef,
                    accompanying.PriorValue, accompanying.ProposedValue));
            }
        }
        else
        {
            return new Refused("Unrecognised change proposal.");
        }

        try
        {
            await _store.AddChangesBatchAsync(id, rows, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A staging failure is a refusal the host can show, not an exception it must catch:
            // SubmitOutcome is the contract's channel for "this did not land, and why".
            return new Refused($"Staging failed: {ex.Message}");
        }

        return new Saved(_revision);
    }

    private static string ChangeTypeFor(string fieldReferenceName) =>
        string.Equals(fieldReferenceName, "System.State", StringComparison.OrdinalIgnoreCase)
            ? "state"
            : "field";
}
