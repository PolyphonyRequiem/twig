using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Reconciliation;
using Twig.Domain.Services.Sync;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>Outcome of the conflict resolution flow.</summary>
internal enum ConflictOutcome
{
    /// <summary>No conflicts, auto-mergeable, or user chose to keep local. Caller should proceed.</summary>
    Proceed,
    /// <summary>User chose to accept remote. Cache already updated. Caller should return 0.</summary>
    AcceptedRemote,
    /// <summary>User chose to abort. Caller should return 0.</summary>
    Aborted,
    /// <summary>JSON conflict output was emitted. Caller should return 1.</summary>
    ConflictJsonEmitted,
}

/// <summary>
/// Encapsulates the CLI-layer conflict resolution orchestration shared by the six
/// mutation commands.
/// </summary>
/// <remarks>
/// Wayfinder 0004 slice 3 routed this through
/// <see cref="ThreeWayMerge"/> rather than two-way <see cref="ConflictResolver"/>. The
/// <paramref name="pendingChangeStore"/> is REQUIRED, not optional, for the same reason 0004 §4
/// deleted the nullable <c>IPendingChangeStore?</c> overloads: the merge base is what makes the
/// merge correct, so a caller that cannot supply one must fail to compile rather than silently
/// degrade to comparing the cache mirror against remote — two snapshots of the same side.
/// </remarks>
internal static class ConflictResolutionFlow
{
    /// <summary>
    /// Detects conflicts between <paramref name="local"/> and <paramref name="remote"/> against
    /// the durable merge base for the item, prompts the user if a genuine conflict remains, and
    /// applies the resolution.
    /// </summary>
    internal static async Task<ConflictOutcome> ResolveAsync(
        WorkItem local,
        WorkItem remote,
        IOutputFormatter fmt,
        string outputFormat,
        IConsoleInput consoleInput,
        IWorkItemRepository workItemRepo,
        IPendingChangeStore pendingChangeStore,
        string acceptRemoteMessage,
        Func<Task>? onAcceptRemote = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pendingChangeStore);

        var staged = await pendingChangeStore.GetChangesAsync(local.Id, ct);
        var mergeBase = MergeBase.FromPendingChanges(staged);

        var mergeResult = ThreeWayMerge.Resolve(local, remote, mergeBase);
        if (mergeResult is not HasConflicts conflicts)
            return ConflictOutcome.Proceed;

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                JsonConflictFormatter.FormatConflictsAsJson(conflicts.ConflictingFields));
            return ConflictOutcome.ConflictJsonEmitted;
        }

        foreach (var c in conflicts.ConflictingFields)
            Console.Error.WriteLine(
                fmt.FormatError($"Conflict on '{c.FieldName}': local='{c.LocalValue}', remote='{c.RemoteValue}'"));

        Console.Write("Keep [l]ocal, [r]emote, or [a]bort? ");
        var choice = consoleInput.ReadLine()?.Trim().ToLowerInvariant();

        if (choice == "a" || choice is null)
        {
            Console.WriteLine("Aborted.");
            return ConflictOutcome.Aborted;
        }

        if (choice == "r")
        {
            if (onAcceptRemote is not null)
                await onAcceptRemote();
            await workItemRepo.SaveAsync(remote);
            Console.WriteLine(acceptRemoteMessage);
            return ConflictOutcome.AcceptedRemote;
        }

        if (choice == "l")
            return ConflictOutcome.Proceed;

        Console.WriteLine("Unrecognized input. Aborted.");
        return ConflictOutcome.Aborted;
    }
}