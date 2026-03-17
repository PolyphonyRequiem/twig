using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig edit [field]</c>: generates a text temp file with current field values,
/// launches editor, parses changes, and stores them as pending.
/// </summary>
public sealed class EditCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Edit work item fields in an external editor.</summary>
    public async Task<int> ExecuteAsync(string? field = null, string outputFormat = "human")
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (resolved is ActiveItemResult.NoContext)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }
        if (resolved is ActiveItemResult.Unreachable u)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{u.Id} not found in cache."));
            return 1;
        }

        var item = resolved switch
        {
            ActiveItemResult.Found f => f.WorkItem,
            ActiveItemResult.FetchedFromAdo f => f.WorkItem,
            _ => null,
        };
        if (item is null)
            return 1;

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
        var changesFound = 0;
        foreach (var line in edited.Split('\n'))
        {
            if (line.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(line))
                continue;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var fieldName = line[..colonIndex].Trim();
            var newValue = line[(colonIndex + 1)..].Trim();

            // Compare with original
            string? originalValue = fieldName switch
            {
                "Title" => item.Title,
                "State" => item.State,
                "AssignedTo" => item.AssignedTo ?? "",
                _ => item.Fields.TryGetValue(fieldName, out var v) ? v : null,
            };

            if (!string.Equals(originalValue, newValue, StringComparison.Ordinal))
            {
                var systemField = fieldName switch
                {
                    "Title" => "System.Title",
                    "State" => "System.State",
                    "AssignedTo" => "System.AssignedTo",
                    _ => fieldName,
                };

                await pendingChangeStore.AddChangeAsync(
                    item.Id,
                    "field",
                    systemField,
                    originalValue,
                    newValue);
                changesFound++;
            }
        }

        if (changesFound == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No changes detected."));
            return 0;
        }

        // Mark dirty
        item.UpdateField("_edited", "true");
        item.ApplyCommands();
        await workItemRepo.SaveAsync(item);

        Console.WriteLine(fmt.FormatSuccess($"Staged {changesFound} change(s) for #{item.Id}."));

        promptStateWriter?.WritePromptState();

        var hints = hintEngine.GetHints("edit", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}
