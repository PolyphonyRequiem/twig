using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Represents a field-level conflict between local and remote work item state.
/// </summary>
public readonly record struct FieldConflict(string FieldName, string? LocalValue, string? RemoteValue);

/// <summary>
/// Discriminated result of a merge conflict resolution.
/// </summary>
public abstract record MergeResult
{
    private MergeResult() { }

    /// <summary>Revisions match — no changes needed.</summary>
    public sealed record NoConflict : MergeResult;

    /// <summary>Different fields changed — can be merged automatically.</summary>
    public sealed record AutoMergeable(IReadOnlyList<string> MergedFields) : MergeResult;

    /// <summary>Same field diverged — requires user resolution.</summary>
    public sealed record HasConflicts(IReadOnlyList<FieldConflict> ConflictingFields) : MergeResult;
}

/// <summary>
/// Compares local and remote work item states to detect and classify merge conflicts.
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Resolves conflicts between <paramref name="local"/> and <paramref name="remote"/> work items.
    /// Compares revisions first; if they match, returns <see cref="MergeResult.NoConflict"/>.
    /// Otherwise, diffs first-class properties (Title, State, etc.) and the Fields dictionary
    /// to determine if changes can be auto-merged or have true conflicts.
    /// </summary>
    public static MergeResult Resolve(WorkItem local, WorkItem remote)
    {
        if (local.Revision == remote.Revision)
            return new MergeResult.NoConflict();

        var autoMerged = new List<string>();
        var conflicts = new List<FieldConflict>();

        // Compare first-class properties — these are NOT tracked in the Fields dictionary
        // but can diverge between local (user-modified) and remote (ADO-updated) state.
        CompareProperty("System.Title", local.Title, remote.Title, conflicts);
        CompareProperty("System.State", local.State, remote.State, conflicts);
        CompareProperty("System.AssignedTo", local.AssignedTo, remote.AssignedTo, conflicts);
        CompareProperty("System.IterationPath", local.IterationPath.Value, remote.IterationPath.Value, conflicts);
        CompareProperty("System.AreaPath", local.AreaPath.Value, remote.AreaPath.Value, conflicts);
        CompareProperty("System.Parent", local.ParentId?.ToString(), remote.ParentId?.ToString(), conflicts);

        // Compare Fields dictionary (arbitrary field storage)
        var localFields = local.Fields;
        var remoteFields = remote.Fields;

        // Collect all field names from both sides
        var allFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in localFields.Keys)
            allFieldNames.Add(key);
        foreach (var key in remoteFields.Keys)
            allFieldNames.Add(key);

        foreach (var fieldName in allFieldNames)
        {
            localFields.TryGetValue(fieldName, out var localValue);
            remoteFields.TryGetValue(fieldName, out var remoteValue);

            var existsInLocal = localFields.ContainsKey(fieldName);
            var existsInRemote = remoteFields.ContainsKey(fieldName);

            // Both have the field with same value — no conflict
            if (existsInLocal && existsInRemote && string.Equals(localValue, remoteValue, StringComparison.Ordinal))
                continue;

            // Only one side has the field — auto-mergeable
            if (existsInLocal && !existsInRemote)
            {
                autoMerged.Add(fieldName);
                continue;
            }

            if (!existsInLocal && existsInRemote)
            {
                autoMerged.Add(fieldName);
                continue;
            }

            // Both sides have the field with different values — conflict
            conflicts.Add(new FieldConflict(fieldName, localValue, remoteValue));
        }

        if (conflicts.Count > 0)
            return new MergeResult.HasConflicts(conflicts);

        if (autoMerged.Count > 0)
            return new MergeResult.AutoMergeable(autoMerged);

        // Revisions differ but no field differences detected
        return new MergeResult.NoConflict();
    }

    /// <summary>
    /// Compares a single first-class property between local and remote.
    /// If values differ (regardless of which side changed), records a conflict.
    /// If values match, no action is taken.
    /// </summary>
    private static void CompareProperty(
        string fieldName,
        string? localValue,
        string? remoteValue,
        List<FieldConflict> conflicts)
    {
        if (string.Equals(localValue, remoteValue, StringComparison.Ordinal))
            return;

        // Values differ between local and remote — report as conflict.
        // Without a shared baseline revision we cannot determine which side changed,
        // so we conservatively flag any divergence to prevent silent data loss.
        conflicts.Add(new FieldConflict(fieldName, localValue, remoteValue));
    }
}
