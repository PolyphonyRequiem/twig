using System.Text.RegularExpressions;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig log</c>: parses git log, extracts work item IDs from commit messages,
/// annotates entries with work item type/state from cache.
/// Supports <c>--count</c> and <c>--work-item</c> filter flags.
/// </summary>
public sealed class LogCommand(
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IGitService? gitService = null)
{
    // Matches #NNN or AB#NNN patterns in commit messages
    private static readonly Regex WorkItemIdPattern = new(
        @"(?:#|AB#)(\d+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Show annotated git log entries.</summary>
    public async Task<int> ExecuteAsync(
        int count = 20,
        int? workItem = null,
        string outputFormat = "human")
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 1. Check git availability
        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, fmt);
        if (!isValid) return exitCode;

        // 2. Get git log entries (hash + message)
        // Use a tab separator (%x09) between the full hash and subject so that spaces
        // inside the commit subject don't interfere with parsing.
        IReadOnlyList<string> logEntries;
        try
        {
            logEntries = await gitService!.GetLogAsync(count, "%H%x09%s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Git log failed: {ex.Message}"));
            return 1;
        }

        if (logEntries.Count == 0)
        {
            Console.Error.WriteLine(fmt.FormatInfo("No commits found."));
            return 0;
        }

        // 3. Parse entries and extract work item IDs
        var parsed = new List<LogEntry>(logEntries.Count);
        foreach (var entry in logEntries)
        {
            var tabIdx = entry.IndexOf('\t');
            if (tabIdx <= 0)
            {
                parsed.Add(new LogEntry(entry, "", null));
                continue;
            }

            var hash = entry[..tabIdx];
            var message = entry[(tabIdx + 1)..];
            var ids = ExtractWorkItemIds(message);
            parsed.Add(new LogEntry(hash, message, ids.Count > 0 ? ids : null));
        }

        // 4. Filter by --work-item if specified
        if (workItem.HasValue)
        {
            parsed = parsed.Where(e =>
                e.WorkItemIds is not null && e.WorkItemIds.Contains(workItem.Value)).ToList();
        }

        // 5. Batch-lookup work items for annotation
        var allIds = new HashSet<int>();
        foreach (var entry in parsed)
        {
            if (entry.WorkItemIds is not null)
            {
                foreach (var id in entry.WorkItemIds)
                    allIds.Add(id);
            }
        }

        var workItemCache = new Dictionary<int, (string Type, string State)>();
        foreach (var id in allIds)
        {
            var item = await workItemRepo.GetByIdAsync(id);
            if (item is not null)
                workItemCache[id] = (item.Type.Value, item.State);
        }

        // 6. Output
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(FormatJsonLog(parsed, workItemCache));
        }
        else
        {
            foreach (var entry in parsed)
            {
                string? type = null, state = null;
                int? annotatedId = null;

                if (entry.WorkItemIds is not null)
                {
                    foreach (var id in entry.WorkItemIds)
                    {
                        if (workItemCache.TryGetValue(id, out var cached))
                        {
                            type = cached.Type;
                            state = cached.State;
                            annotatedId = id;
                            break;
                        }
                    }

                    // If no cached data found, still show the first ID
                    annotatedId ??= entry.WorkItemIds[0];
                }

                Console.WriteLine(fmt.FormatAnnotatedLogEntry(
                    entry.Hash, entry.Message, type, state, annotatedId));
            }

            var hints = hintEngine.GetHints("log", outputFormat: outputFormat);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }

        return 0;
    }

    /// <summary>Extract all work item IDs from a commit message.</summary>
    internal static List<int> ExtractWorkItemIds(string message)
    {
        var ids = new List<int>();
        var matches = WorkItemIdPattern.Matches(message);
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }

    private static string FormatJsonLog(
        List<LogEntry> entries,
        Dictionary<int, (string Type, string State)> workItemCache)
    {
        using var stream = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("command", "log");
        writer.WriteStartArray("entries");

        foreach (var entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteString("hash", entry.Hash);
            writer.WriteString("message", entry.Message);

            if (entry.WorkItemIds is not null && entry.WorkItemIds.Count > 0)
            {
                writer.WriteStartArray("workItems");
                foreach (var id in entry.WorkItemIds)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    if (workItemCache.TryGetValue(id, out var cached))
                    {
                        writer.WriteString("type", cached.Type);
                        writer.WriteString("state", cached.State);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStartArray("workItems");
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("exitCode", 0);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record LogEntry(string Hash, string Message, List<int>? WorkItemIds);
}
