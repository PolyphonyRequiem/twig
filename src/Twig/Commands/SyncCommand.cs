using System.Text.Json;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig sync</c>: flush pending changes to ADO (push), then refresh the local cache (pull).
/// Delegates push to <see cref="IPendingChangeFlusher"/> and pull to <see cref="RefreshCommand"/>.
/// </summary>
public sealed class SyncCommand(
    IPendingChangeFlusher pendingChangeFlusher,
    RefreshCommand refreshCommand,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null)
{
    private static readonly JsonWriterOptions JsonWriterOptions = new() { Indented = true };
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Flush pending changes then refresh the local cache.</summary>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="force">When true, pass --force to the refresh phase.</param>
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool force = false,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // ── Phase 1: Push ──────────────────────────────────────────
        var flushResult = await pendingChangeFlusher.FlushAllAsync(outputFormat, ct);

        if (flushResult.Failures.Count > 0)
        {
            foreach (var failure in flushResult.Failures)
                _stderr.WriteLine(fmt.FormatError($"Flush failed for #{failure.ItemId}: {failure.Error}"));
        }

        // ── Phase 2: Pull ──────────────────────────────────────────
        var refreshExitCode = await refreshCommand.ExecuteAsync(outputFormat, force, ct);

        // ── Output summary ─────────────────────────────────────────
        var hasFlushFailures = flushResult.Failures.Count > 0;
        var exitCode = hasFlushFailures || refreshExitCode != 0 ? 1 : 0;

        if (outputFormat.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            WriteSyncJson(flushResult, refreshExitCode);
        }
        else
        {
            if (flushResult.ItemsFlushed > 0 || hasFlushFailures)
            {
                Console.WriteLine(fmt.FormatSuccess(
                    $"Sync push: {flushResult.ItemsFlushed} flushed, {flushResult.Failures.Count} failed."));
            }
            else
            {
                Console.WriteLine(fmt.FormatInfo("Sync push: nothing to flush."));
            }
        }

        return exitCode;
    }

    private static void WriteSyncJson(FlushResult flush, int refreshExitCode)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, JsonWriterOptions);

        writer.WriteStartObject();

        writer.WritePropertyName("flush");
        writer.WriteStartObject();
        writer.WriteNumber("flushed", flush.ItemsFlushed);
        writer.WriteNumber("fieldChangesPushed", flush.FieldChangesPushed);
        writer.WriteNumber("notesPushed", flush.NotesPushed);
        writer.WriteNumber("failed", flush.Failures.Count);

        if (flush.Failures.Count > 0)
        {
            writer.WritePropertyName("failures");
            writer.WriteStartArray();
            foreach (var f in flush.Failures)
            {
                writer.WriteStartObject();
                writer.WriteNumber("itemId", f.ItemId);
                writer.WriteString("error", f.Error);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();

        writer.WritePropertyName("refresh");
        writer.WriteStartObject();
        writer.WriteNumber("exitCode", refreshExitCode);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();

        Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}
