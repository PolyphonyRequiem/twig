using System.Text.Json.Serialization;

namespace Twig.Domain.ValueObjects;

internal sealed record DraftFieldChange
{
    [JsonConstructor]
    public DraftFieldChange(string fieldRef, string? baselineValue, string? proposedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldRef);
        FieldRef = fieldRef;
        BaselineValue = baselineValue;
        ProposedValue = proposedValue;
    }

    public string FieldRef { get; }
    public string? BaselineValue { get; }
    public string? ProposedValue { get; }
}

internal sealed record DraftFieldConflict
{
    [JsonConstructor]
    public DraftFieldConflict(string fieldRef, string? baselineValue, string? observedValue, string? proposedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldRef);
        FieldRef = fieldRef;
        BaselineValue = baselineValue;
        ObservedValue = observedValue;
        ProposedValue = proposedValue;
    }

    public string FieldRef { get; }
    public string? BaselineValue { get; }
    public string? ObservedValue { get; }
    public string? ProposedValue { get; }
}

/// <summary>
/// The one recoverable draft shared by native work-item surfaces for a stable item key.
/// Its original baseline is retained when newer revisions are observed.
/// </summary>
internal sealed class RecoverableWorkItemDraft
{
    [JsonConstructor]
    public RecoverableWorkItemDraft(
        QualifiedWorkItemKey itemKey,
        int baselineRevision,
        int observedRevision,
        long generation,
        IReadOnlyList<DraftFieldChange> changes,
        IReadOnlyList<DraftFieldConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(conflicts);
        if (baselineRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(baselineRevision));
        if (observedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(observedRevision));
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        ItemKey = itemKey;
        BaselineRevision = baselineRevision;
        ObservedRevision = observedRevision;
        Generation = generation;
        Changes = Array.AsReadOnly(changes.ToArray());
        Conflicts = Array.AsReadOnly(conflicts.ToArray());
    }

    public QualifiedWorkItemKey ItemKey { get; }
    public int BaselineRevision { get; }
    public int ObservedRevision { get; }
    public long Generation { get; }
    public IReadOnlyList<DraftFieldChange> Changes { get; }
    public IReadOnlyList<DraftFieldConflict> Conflicts { get; }
    public bool HasRevisionDrift => ObservedRevision != BaselineRevision;
    public bool HasConflicts => HasRevisionDrift || Conflicts.Count > 0;

    public static RecoverableWorkItemDraft Create(QualifiedWorkItemKey itemKey, int baselineRevision)
        => new(itemKey, baselineRevision, baselineRevision, 0, [], []);

    /// <summary>
    /// Replaces one proposed field value while retaining its original baseline. Returning
    /// to the baseline removes that effect but still advances the generation.
    /// </summary>
    public RecoverableWorkItemDraft SetField(string fieldRef, string? baselineValue, string? proposedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldRef);
        var changes = Changes.ToList();
        var index = changes.FindIndex(change =>
            string.Equals(change.FieldRef, fieldRef, StringComparison.OrdinalIgnoreCase));
        var originalValue = index >= 0 ? changes[index].BaselineValue : baselineValue;
        if (index >= 0)
            changes.RemoveAt(index);

        if (!string.Equals(originalValue, proposedValue, StringComparison.Ordinal))
            changes.Add(new DraftFieldChange(fieldRef, originalValue, proposedValue));

        return new(ItemKey, BaselineRevision, ObservedRevision, checked(Generation + 1),
            changes.OrderBy(change => change.FieldRef, StringComparer.Ordinal).ToArray(), Conflicts);
    }

    /// <summary>
    /// Records a newer native observation without rebasing the baseline or dropping edits.
    /// Conflicts are explicit until a later native reconciliation replaces them.
    /// </summary>
    public RecoverableWorkItemDraft Observe(
        int observedRevision,
        IReadOnlyList<DraftFieldConflict>? conflicts = null)
    {
        if (observedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(observedRevision));
        var nextConflicts = conflicts?.ToArray() ?? [];
        if (observedRevision == ObservedRevision && Conflicts.SequenceEqual(nextConflicts))
            return this;

        return new(ItemKey, BaselineRevision, observedRevision, checked(Generation + 1), Changes, nextConflicts);
    }
}
