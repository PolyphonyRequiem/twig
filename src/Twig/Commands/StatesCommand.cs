using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig states</c>: returns available workflow states (name, category, color)
/// for the active work item's type. Reads from the local process type cache — no network call.
/// Designed for extension consumption via <c>--output json</c>.
/// </summary>
public sealed class StatesCommand(
    ActiveItemResolver activeItemResolver,
    IProcessTypeStore processTypeStore,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        var typeRecord = await processTypeStore.GetByNameAsync(item.Type.Value, ct);
        if (typeRecord is null || typeRecord.States.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError($"No states found for type '{item.Type.Value}'. Run 'twig sync' to refresh process data."));
            return 1;
        }

        if (fmt is JsonOutputFormatter)
        {
            Console.WriteLine(FormatStatesJson(typeRecord.States, item.Type.Value));
        }
        else
        {
            foreach (var state in typeRecord.States)
            {
                var color = state.Color is not null ? $" (#{state.Color})" : "";
                Console.WriteLine($"  {state.Name,-20} {state.Category}{color}");
            }
        }

        return 0;
    }

    private static string FormatStatesJson(
        IReadOnlyList<Domain.ValueObjects.StateEntry> states,
        string typeName)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("type", typeName);
        writer.WriteStartArray("states");

        foreach (var state in states)
        {
            writer.WriteStartObject();
            writer.WriteString("name", state.Name);
            writer.WriteString("category", state.Category.ToString());
            if (state.Color is not null)
                writer.WriteString("color", state.Color);
            else
                writer.WriteNull("color");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
