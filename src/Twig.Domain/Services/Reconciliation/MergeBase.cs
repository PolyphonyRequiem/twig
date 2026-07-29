using Twig.Domain.Common;

namespace Twig.Domain.Services.Reconciliation;

/// <summary>
/// The per-field merge base for a work item: the value both sides agreed on at the moment the
/// user staged an edit.
/// </summary>
/// <remarks>
/// <para>
/// Wayfinder <b>0006</b> ruled that twig must NOT persist a baseline revision, because the
/// baseline already exists, is already durable, and is already finer-grained than an integer
/// revision: it is <c>pending_changes.old_value</c>. This type is that ruling made
/// addressable — it carries no new state and no new storage, only a projection of rows the
/// pending store already holds.
/// </para>
/// <para>
/// It exists because <see cref="Sync.ConflictResolver.Resolve(Aggregates.WorkItem, Aggregates.WorkItem)"/>
/// has no merge base and says so in its own code: <i>"Without a shared baseline revision we
/// cannot determine which side changed, so we conservatively flag any divergence."</i> The
/// deeper problem is that its <c>local</c> argument does not contain the local edit at all.
/// Staging writes the user's edit to <c>pending_changes</c> and stamps only
/// <c>_edited=true</c> on the aggregate (<c>EditCommand.StageLocallyAsync</c>); the edited
/// properties are <c>init</c>-only on <see cref="Aggregates.WorkItem"/>, so there is no
/// mutator to apply an edit through. Two-argument <c>Resolve</c> therefore compares the
/// last-synced cache mirror against fresh remote — two snapshots of the <b>same side</b>.
/// </para>
/// <para>
/// With this base, <see cref="ThreeWayMerge"/> can finally distinguish the three cases a
/// two-way diff collapses into one: only-local moved, only-remote moved, and both moved.
/// </para>
/// </remarks>
public sealed class MergeBase
{
    /// <summary>Change types that carry a field-level edit. Legacy aliases are honoured for the
    /// same reason <c>IPendingChangeStore.GetChangeSummaryAsync</c> honours them — rows written
    /// by older twig versions must keep counting (PolyphonyRequiem/twig#251).</summary>
    private static readonly HashSet<string> FieldChangeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "field", "state", "set_field" };

    private readonly Dictionary<string, FieldMergeBase> _byField;

    private MergeBase(Dictionary<string, FieldMergeBase> byField) => _byField = byField;

    /// <summary>A merge base with no staged edits — every field is unchanged locally.</summary>
    public static MergeBase Empty { get; } =
        new(new Dictionary<string, FieldMergeBase>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Projects the staged field edits for one work item into a merge base.
    /// </summary>
    /// <remarks>
    /// Note rows (<c>note</c>/<c>add_note</c>) are excluded: a note is an append, not a field
    /// edit, so it can never conflict with a remote field value and must not be presented as
    /// though it could.
    /// <para>
    /// When a field was edited more than once the <b>first</b> row's <c>OldValue</c> is the
    /// base — that is the value at the last sync — while the <b>last</b> row's
    /// <c>NewValue</c> is the current local intent. Collapsing to the latest row alone would
    /// move the base forward onto the user's own intermediate edit and make a genuine remote
    /// divergence look like agreement.
    /// </para>
    /// </remarks>
    public static MergeBase FromPendingChanges(IEnumerable<PendingChangeRecord> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var byField = new Dictionary<string, FieldMergeBase>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            if (!FieldChangeTypes.Contains(change.ChangeType))
                continue;
            if (string.IsNullOrWhiteSpace(change.FieldName))
                continue;

            if (byField.TryGetValue(change.FieldName, out var existing))
            {
                // Keep the earliest BaseValue, advance LocalValue to the newest edit.
                byField[change.FieldName] = existing with { LocalValue = change.NewValue };
            }
            else
            {
                byField[change.FieldName] = new FieldMergeBase(change.OldValue, change.NewValue);
            }
        }

        return byField.Count == 0 ? Empty : new MergeBase(byField);
    }

    /// <summary>True when no field edits are staged, so nothing can have moved locally.</summary>
    public bool IsEmpty => _byField.Count == 0;

    /// <summary>The field names carrying a staged local edit.</summary>
    public IReadOnlyCollection<string> StagedFields => _byField.Keys;

    /// <summary>
    /// The staged base and local intent for <paramref name="fieldName"/>, or <see langword="null"/>
    /// when the user has not edited that field — in which case the local side has not moved and
    /// the remote value can be taken without asking.
    /// </summary>
    public FieldMergeBase? For(string fieldName) =>
        _byField.TryGetValue(fieldName, out var value) ? value : null;
}

/// <summary>
/// One field's merge base: the value at the last sync (<paramref name="BaseValue"/>) and the
/// value the user staged over it (<paramref name="LocalValue"/>).
/// </summary>
public readonly record struct FieldMergeBase(string? BaseValue, string? LocalValue);
