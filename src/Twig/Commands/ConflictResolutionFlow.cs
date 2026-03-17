using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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
/// Encapsulates the CLI-layer conflict resolution orchestration shared by
/// StateCommand, UpdateCommand, and SaveCommand.
/// </summary>
internal static class ConflictResolutionFlow
{
    /// <summary>
    /// Detects conflicts between <paramref name="local"/> and <paramref name="remote"/>,
    /// prompts the user if needed, and applies the resolution.
    /// </summary>
    internal static async Task<ConflictOutcome> ResolveAsync(
        WorkItem local,
        WorkItem remote,
        IOutputFormatter fmt,
        string outputFormat,
        IConsoleInput consoleInput,
        IWorkItemRepository workItemRepo,
        string acceptRemoteMessage,
        Func<Task>? onAcceptRemote = null)
    {
        var mergeResult = ConflictResolver.Resolve(local, remote);
        if (mergeResult is not MergeResult.HasConflicts conflicts)
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
            Console.WriteLine(fmt.FormatInfo("Aborted."));
            return ConflictOutcome.Aborted;
        }

        if (choice == "r")
        {
            if (onAcceptRemote is not null)
                await onAcceptRemote();
            await workItemRepo.SaveAsync(remote);
            Console.WriteLine(fmt.FormatSuccess(acceptRemoteMessage));
            return ConflictOutcome.AcceptedRemote;
        }

        // 'l' or any unrecognized input: proceed with local changes
        return ConflictOutcome.Proceed;
    }
}
