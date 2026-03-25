using System.Text;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig nav back</c>, <c>twig nav fore</c>, and <c>twig nav history</c>:
/// chronological navigation through the context-switch history.
/// Back/fore set context directly (DD-04) to avoid recording new history entries.
/// Negative seed IDs are resolved at read time (DD-05) via <see cref="IPublishIdMapRepository"/>.
/// </summary>
public sealed class NavigationHistoryCommands(
    INavigationHistoryStore historyStore,
    IPublishIdMapRepository publishIdMapRepo,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    OutputFormatterFactory formatterFactory,
    RenderingPipelineFactory? pipelineFactory = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Navigate backward in the navigation history.</summary>
    public async Task<int> BackAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var workItemId = await historyStore.GoBackAsync(ct);
        if (workItemId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Already at oldest entry in navigation history."));
            return 1;
        }

        // DD-05: Resolve seed IDs at read time
        var resolvedId = await ResolveSeedIdAsync(workItemId.Value, ct);

        // DD-04: Set context directly (bypass SetCommand to avoid recording history)
        await contextStore.SetActiveWorkItemIdAsync(resolvedId, ct);

        var item = await workItemRepo.GetByIdAsync(resolvedId, ct);
        if (item is not null)
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
        else
            Console.WriteLine(fmt.FormatInfo($"#{resolvedId}"));

        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    /// <summary>Navigate forward in the navigation history.</summary>
    public async Task<int> ForeAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var workItemId = await historyStore.GoForwardAsync(ct);
        if (workItemId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Already at newest entry in navigation history."));
            return 1;
        }

        // DD-05: Resolve seed IDs at read time
        var resolvedId = await ResolveSeedIdAsync(workItemId.Value, ct);

        // DD-04: Set context directly (bypass SetCommand to avoid recording history)
        await contextStore.SetActiveWorkItemIdAsync(resolvedId, ct);

        var item = await workItemRepo.GetByIdAsync(resolvedId, ct);
        if (item is not null)
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
        else
            Console.WriteLine(fmt.FormatInfo($"#{resolvedId}"));

        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    /// <summary>Display the navigation history with an optional interactive picker.</summary>
    public async Task<int> HistoryAsync(bool nonInteractive, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        var (entries, cursorEntryId) = await historyStore.GetHistoryAsync(ct);

        if (entries.Count == 0)
        {
            Console.Error.WriteLine(fmt.FormatInfo("Navigation history is empty."));
            return 0;
        }

        // DD-05: Resolve seed IDs at read time and enrich with work item data (best-effort)
        var enriched = new List<(NavigationHistoryEntry Entry, int ResolvedId, string? TypeName, string? Title, string? State)>(entries.Count);
        foreach (var entry in entries)
        {
            var resolvedId = await ResolveSeedIdAsync(entry.WorkItemId, ct);
            var item = await workItemRepo.GetByIdAsync(resolvedId, ct);
            enriched.Add((entry, resolvedId, item?.Type.ToString(), item?.Title, item?.State));
        }

        // JSON output format
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var (entry, resolvedId, _, _, _) in enriched)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", entry.Id);
                writer.WriteNumber("workItemId", resolvedId);
                writer.WriteString("visitedAt", entry.VisitedAt.ToString("o"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (cursorEntryId.HasValue)
                writer.WriteNumber("currentEntryId", cursorEntryId.Value);
            else
                writer.WriteNull("currentEntryId");
            writer.WriteEndObject();

            writer.Flush();
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            return 0;
        }

        // Minimal output format: one work item ID per line
        if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (_, resolvedId, _, _, _) in enriched)
                Console.WriteLine(resolvedId);
            return 0;
        }

        // Human format — interactive or non-interactive
        var isInteractive = renderer is not null && !nonInteractive;

        if (isInteractive)
        {
            // Interactive picker via PromptDisambiguationAsync (AOT-safe Live() pattern)
            var matches = new List<(int Id, string Title)>(enriched.Count);
            foreach (var (entry, resolvedId, typeName, title, state) in enriched)
            {
                var marker = entry.Id == cursorEntryId ? "→ " : "  ";
                var display = title is not null
                    ? $"{marker}{typeName} — {title} [{state}]  {entry.VisitedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                    : $"{marker}#{resolvedId}  {entry.VisitedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
                matches.Add((resolvedId, display));
            }

            var selected = await renderer!.PromptDisambiguationAsync(matches, ct);
            if (selected is not null)
            {
                // FR-08: On selection, record new history entry (prune forward) and set context
                var selectedId = selected.Value.Id;
                await historyStore.RecordVisitAsync(selectedId, ct);
                await contextStore.SetActiveWorkItemIdAsync(selectedId, ct);

                var selectedItem = await workItemRepo.GetByIdAsync(selectedId, ct);
                if (selectedItem is not null)
                    Console.WriteLine(fmt.FormatWorkItem(selectedItem, showDirty: false));
                else
                    Console.WriteLine(fmt.FormatInfo($"#{selectedId}"));

                if (promptStateWriter is not null)
                    await promptStateWriter.WritePromptStateAsync();
            }

            return 0;
        }

        // Non-interactive flat list
        Console.WriteLine($"Navigation History ({entries.Count} entries):");
        foreach (var (entry, resolvedId, typeName, title, state) in enriched)
        {
            var marker = entry.Id == cursorEntryId ? "→" : " ";
            var display = title is not null
                ? $"  {marker} #{resolvedId,-5} ● {typeName} — {title} [{state}]"
                : $"  {marker} #{resolvedId,-5}";
            var timestamp = entry.VisitedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            Console.WriteLine($"{display}  {timestamp}");
        }

        return 0;
    }

    /// <summary>
    /// Resolves a seed ID to its published ADO ID if available.
    /// Negative IDs indicate local seeds; if published, the mapping is returned.
    /// Otherwise the original ID is returned unchanged.
    /// </summary>
    private async Task<int> ResolveSeedIdAsync(int workItemId, CancellationToken ct)
    {
        if (workItemId < 0)
        {
            var newId = await publishIdMapRepo.GetNewIdAsync(workItemId, ct);
            if (newId.HasValue)
                return newId.Value;
        }

        return workItemId;
    }
}
