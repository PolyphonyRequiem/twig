using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig edit [field]</c>: generates a text temp file with current field values,
/// launches editor, parses changes. For non-seed items, pushes immediately to ADO with
/// conflict resolution and offline fallback. For seeds, stages locally.
/// </summary>
public sealed class EditCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IAdoWorkItemService adoService,
    IConsoleInput consoleInput,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Edit work item fields in an external editor.</summary>
    public async Task<int> ExecuteAsync(string? field = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            Console.Error.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // Generate editable content (YAML-like format per RD-037)
        string initialContent;
        if (field is not null)
        {
            item.Fields.TryGetValue(field, out var currentValue);
            initialContent = $"# Editing {field} for #{item.Id} {item.Title}\n{field}: {currentValue ?? ""}\n";
        }
        else
        {
            initialContent = $"# Editing #{item.Id} {item.Title}\n"
                + $"# Change values below. Lines starting with # are ignored.\n"
                + $"Title: {item.Title}\n"
                + $"State: {item.State}\n"
                + $"AssignedTo: {item.AssignedTo ?? ""}\n";
        }

        var edited = await editorLauncher.LaunchAsync(initialContent);
        if (edited is null)
        {
            Console.WriteLine(fmt.FormatInfo("Edit cancelled (unchanged or editor aborted)."));
            return 0;
        }

        // Parse changes from edited content
        var parsedChanges = new List<FieldChange>();
        foreach (var line in edited.Split('\n'))
        {
            if (line.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(line))
                continue;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var fieldName = line[..colonIndex].Trim();
            var newValue = line[(colonIndex + 1)..].Trim();

            var systemField = fieldName is "Title" or "State" or "AssignedTo"
                ? $"System.{fieldName}" : fieldName;
            string? originalValue = fieldName switch
            {
                "Title" => item.Title,
                "State" => item.State,
                "AssignedTo" => item.AssignedTo ?? "",
                _ => item.Fields.TryGetValue(fieldName, out var v) ? v : null,
            };
            if (!string.Equals(originalValue, newValue, StringComparison.Ordinal))
                parsedChanges.Add(new FieldChange(systemField, originalValue, newValue));
        }

        if (parsedChanges.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No changes detected."));
            return 0;
        }

        string successMessage;

        if (!item.IsSeed)
        {
            // Push-on-write: push field changes directly to ADO
            try
            {
                var remote = await adoService.FetchAsync(item.Id, ct);

                var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
                    item, remote, fmt, outputFormat, consoleInput, workItemRepo,
                    $"#{item.Id} updated from remote.");
                if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
                    return 1;
                if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
                    return 0;

                await ConflictRetryHelper.PatchWithRetryAsync(
                    adoService, item.Id, parsedChanges, remote.Revision, ct);

                // Auto-push notes in its own scope — failures must not trigger staging-fallback
                // since field changes are already in ADO (NFR-2)
                try
                {
                    await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Note push failed (fields already pushed): {ex.Message}");
                }

                // DD-8: Resync failure after successful push — warn, do NOT stage locally
                try
                {
                    var updated = await adoService.FetchAsync(item.Id, ct);
                    await workItemRepo.SaveAsync(updated, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine(
                        $"Changes pushed but cache may be stale — run 'twig sync' to resync ({ex.Message})");
                }

                successMessage = fmt.FormatSuccess($"Pushed {parsedChanges.Count} change(s) for #{item.Id}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Push failed — fall back to local staging
                Console.Error.WriteLine($"Changes staged locally (push failed): {ex.Message}");
                await StageLocallyAsync(item, parsedChanges, ct);
                successMessage = fmt.FormatSuccess($"Staged {parsedChanges.Count} change(s) for #{item.Id}.");
            }
        }
        else
        {
            // Seed items: stage locally (existing behavior)
            await StageLocallyAsync(item, parsedChanges, ct);
            successMessage = fmt.FormatSuccess($"Staged {parsedChanges.Count} change(s) for #{item.Id}.");
        }

        Console.WriteLine(successMessage);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        var hints = hintEngine.GetHints("edit", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    private async Task StageLocallyAsync(
        Domain.Aggregates.WorkItem item,
        List<FieldChange> changes,
        CancellationToken ct)
    {
        foreach (var change in changes)
        {
            await pendingChangeStore.AddChangeAsync(
                item.Id, "field", change.FieldName, change.OldValue, change.NewValue, ct);
        }

        item.UpdateField("_edited", "true");
        await workItemRepo.SaveAsync(item, ct);
    }
}