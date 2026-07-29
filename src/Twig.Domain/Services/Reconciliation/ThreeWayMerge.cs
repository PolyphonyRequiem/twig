using Twig.Domain.Aggregates;
using Twig.Domain.Services.Sync;

namespace Twig.Domain.Services.Reconciliation;

/// <summary>
/// Classifies local/remote divergence against a durable merge base.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>published → reconciled</c> transition of wayfinder 0004's lifecycle, and the
/// reason the module exists. <see cref="ConflictResolver"/> compares two values and must treat
/// every difference as a conflict; with the base from <see cref="MergeBase"/> this can
/// distinguish the three cases that a two-way diff collapses into one:
/// </para>
/// <list type="table">
///   <item><term>Only remote moved</term><description>base == local ⇒ take remote. Not a conflict.</description></item>
///   <item><term>Only local moved</term><description>base == remote ⇒ keep local. Not a conflict.</description></item>
///   <item><term>Both moved</term><description>a real conflict — and only now is the user asked.</description></item>
/// </list>
/// <para>
/// The narrowing is deliberate and was ruled by the owner: a field the user never touched no
/// longer prompts. Convergent edits (both sides moved to the <i>same</i> value) are likewise
/// not conflicts — there is nothing to choose between.
/// </para>
/// <para>
/// <b>The fail-safe direction matters.</b> When the base is unavailable — no staged edit exists
/// for a diverging field — this does NOT silently take remote. An absent base means the local
/// side cannot be shown to have moved, so the remote value is safe to adopt only for fields the
/// user has no staged intent on. Any field the user <i>did</i> stage is evaluated against its
/// real base, never guessed at. See <see cref="Classify"/>.
/// </para>
/// <para>
/// The result deliberately reuses <see cref="MergeResult"/> so the two surfaces
/// (<c>ConflictResolutionFlow</c> for the CLI, the MCP envelope) keep pattern-matching one
/// vocabulary rather than learning a second one. <see cref="MergeResult"/> is a
/// <c>union</c>: pattern-match the case (<c>result is HasConflicts</c>).
/// </para>
/// </remarks>
public static class ThreeWayMerge
{
    /// <summary>
    /// Field names carried by first-class <see cref="WorkItem"/> properties rather than the
    /// <see cref="WorkItem.Fields"/> dictionary. A staged edit to one of these is recorded in
    /// pending_changes under the same canonical name, so it must be excluded from the Fields
    /// sweep or a single divergence is reported twice.
    /// </summary>
    private static readonly HashSet<string> FirstClassFieldNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "System.Title",
            "System.State",
            "System.AssignedTo",
            "System.IterationPath",
            "System.AreaPath",
            "System.Parent",
        };

    /// <summary>
    /// Resolves <paramref name="local"/> against <paramref name="remote"/> using
    /// <paramref name="mergeBase"/> to determine which side actually moved.
    /// </summary>
    /// <remarks>
    /// Revision equality still short-circuits to <see cref="NoConflict"/>, matching
    /// <see cref="ConflictResolver.Resolve"/>. Note that a freshly constructed
    /// <see cref="WorkItem"/> has <c>Revision = 0</c> on both sides, so any conflict-path
    /// fixture must advance the remote revision with <c>remote.MarkSynced(n)</c> or this
    /// returns before the branch under test ever runs.
    /// </remarks>
    public static MergeResult Resolve(WorkItem local, WorkItem remote, MergeBase mergeBase)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(mergeBase);

        if (local.Revision == remote.Revision)
            return new NoConflict();

        var autoMerged = new List<string>();
        var conflicts = new List<FieldConflict>();

        // First-class properties. These are init-only on WorkItem, so the cache mirror can
        // never carry the user's edit — the merge base is the ONLY source of local intent.
        //
        // A staged edit to one of these lands in pending_changes under the SAME canonical name
        // (e.g. "System.Title"), so it would also be pulled into the Fields sweep below via
        // MergeBase.StagedFields and classified a second time — reporting one divergence as two
        // conflicts, the second against an absent Fields entry. FirstClassFieldNames keeps each
        // field classified exactly once, here, where the authoritative property value lives.
        Classify("System.Title", local.Title, remote.Title, mergeBase, autoMerged, conflicts);
        Classify("System.State", local.State, remote.State, mergeBase, autoMerged, conflicts);
        Classify("System.AssignedTo", local.AssignedTo, remote.AssignedTo, mergeBase, autoMerged, conflicts);
        Classify("System.IterationPath", local.IterationPath.Value, remote.IterationPath.Value, mergeBase, autoMerged, conflicts);
        Classify("System.AreaPath", local.AreaPath.Value, remote.AreaPath.Value, mergeBase, autoMerged, conflicts);
        Classify("System.Parent", local.ParentId?.ToString(), remote.ParentId?.ToString(), mergeBase, autoMerged, conflicts);

        var localFields = local.Fields;
        var remoteFields = remote.Fields;

        var allFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in localFields.Keys)
            allFieldNames.Add(key);
        foreach (var key in remoteFields.Keys)
            allFieldNames.Add(key);
        // A field may be staged locally and absent from both snapshots (a newly set field).
        foreach (var key in mergeBase.StagedFields)
            allFieldNames.Add(key);
        // Already classified above against their authoritative property values.
        allFieldNames.ExceptWith(FirstClassFieldNames);

        foreach (var fieldName in allFieldNames)
        {
            localFields.TryGetValue(fieldName, out var localValue);
            remoteFields.TryGetValue(fieldName, out var remoteValue);

            var existsInLocal = localFields.ContainsKey(fieldName);
            var existsInRemote = remoteFields.ContainsKey(fieldName);

            // Present on exactly one side with no staged intent: additive, nothing to resolve.
            if (existsInLocal != existsInRemote && mergeBase.For(fieldName) is null)
            {
                autoMerged.Add(fieldName);
                continue;
            }

            Classify(fieldName, localValue, remoteValue, mergeBase, autoMerged, conflicts);
        }

        if (conflicts.Count > 0)
            return new HasConflicts(conflicts);

        if (autoMerged.Count > 0)
            return new AutoMergeable(autoMerged);

        return new NoConflict();
    }

    /// <summary>
    /// Classifies one field. <paramref name="localValue"/> is the cache mirror's value, which is
    /// only the user's intent when no edit is staged; when one is,
    /// <see cref="FieldMergeBase.LocalValue"/> supersedes it.
    /// </summary>
    private static void Classify(
        string fieldName,
        string? localValue,
        string? remoteValue,
        MergeBase mergeBase,
        List<string> autoMerged,
        List<FieldConflict> conflicts)
    {
        var staged = mergeBase.For(fieldName);

        // No staged edit ⇒ the local side did not move. The cache mirror IS the last-synced
        // value, so any divergence is remote-only and is taken without asking. This is the
        // narrowing the owner ruled for, and it is safe precisely because an unstaged field
        // has no local intent that could be lost.
        if (staged is null)
        {
            if (!string.Equals(localValue, remoteValue, StringComparison.Ordinal))
                autoMerged.Add(fieldName);
            return;
        }

        var baseValue = staged.Value.BaseValue;
        var localIntent = staged.Value.LocalValue;

        var localMoved = !string.Equals(baseValue, localIntent, StringComparison.Ordinal);
        var remoteMoved = !string.Equals(baseValue, remoteValue, StringComparison.Ordinal);

        if (!remoteMoved)
        {
            // Only the local side moved — the staged edit stands. Nothing to merge or ask.
            return;
        }

        if (!localMoved)
        {
            // A staged row exists but its value equals the base (a no-op edit); remote wins.
            autoMerged.Add(fieldName);
            return;
        }

        // Both sides moved to the SAME value — convergent, so there is nothing to choose.
        if (string.Equals(localIntent, remoteValue, StringComparison.Ordinal))
            return;

        // Both sides moved, and they disagree. This — and only this — is a conflict.
        // The local value reported is the user's staged intent, not the stale mirror.
        conflicts.Add(new FieldConflict(fieldName, localIntent, remoteValue));
    }
}
