using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig process</c>: exposes process type discovery.
/// <list type="bullet">
///   <item>No args → lists all work item types with state counts.</item>
///   <item>With type arg → shows states, fields, and transitions for that type.</item>
/// </list>
/// Also serves as the implementation for the hidden <c>twig states</c> alias,
/// which scopes to the active work item's type for backward compatibility.
/// </summary>
public sealed class ProcessCommand(
    ActiveItemResolver activeItemResolver,
    IProcessTypeStore processTypeStore,
    IFieldDefinitionStore fieldDefinitionStore,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Executes the <c>twig process [type]</c> command.
    /// When <paramref name="typeName"/> is null, lists all types.
    /// When provided, shows details for that specific type.
    /// </summary>
    public async Task<int> ExecuteAsync(
        string? typeName = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        return typeName is null
            ? await ExecuteListAsync(outputFormat, ct)
            : await ExecuteTypeDetailAsync(typeName, outputFormat, ct);
    }

    /// <summary>
    /// Executes the hidden <c>twig states</c> alias: resolves the active work item's type
    /// and shows its states (backward compat with the old StatesCommand).
    /// </summary>
    public async Task<int> ExecuteStatesAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
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

        return await ExecuteTypeDetailAsync(item.Type.Value, outputFormat, ct);
    }

    private async Task<int> ExecuteListAsync(string outputFormat, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var types = await processTypeStore.GetAllAsync(ct);

        if (types.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError("No process types found. Run 'twig sync' to refresh process data."));
            return 1;
        }

        if (fmt is JsonOutputFormatter or JsonCompactOutputFormatter)
        {
            Console.WriteLine(FormatTypesListJson(types));
        }
        else
        {
            foreach (var type in types)
            {
                var color = type.ColorHex is not null ? $" (#{type.ColorHex})" : "";
                Console.WriteLine($"  {type.TypeName,-20} {type.States.Count} states{color}");
            }
        }

        return 0;
    }

    private async Task<int> ExecuteTypeDetailAsync(string typeName, string outputFormat, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var typeRecord = await processTypeStore.GetByNameAsync(typeName, ct);

        if (typeRecord is null || typeRecord.States.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError($"No states found for type '{typeName}'. Run 'twig sync' to refresh process data."));
            return 1;
        }

        var fields = await fieldDefinitionStore.GetAllAsync(ct);

        if (fmt is JsonOutputFormatter or JsonCompactOutputFormatter)
        {
            Console.WriteLine(FormatTypeDetailJson(typeRecord, fields));
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

    private static string FormatTypesListJson(IReadOnlyList<Domain.Aggregates.ProcessTypeRecord> types)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteStartArray("types");

        foreach (var type in types)
        {
            writer.WriteStartObject();
            writer.WriteString("typeName", type.TypeName);
            writer.WriteNumber("stateCount", type.States.Count);
            writer.WriteNumber("childTypeCount", type.ValidChildTypes.Count);
            if (type.ColorHex is not null)
                writer.WriteString("color", type.ColorHex);
            else
                writer.WriteNull("color");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("totalTypes", types.Count);
        writer.WriteEndObject();

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FormatTypeDetailJson(
        Domain.Aggregates.ProcessTypeRecord type,
        IReadOnlyList<FieldDefinition> fields)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("type", type.TypeName);
        writer.WriteStartArray("states");

        foreach (var state in type.States)
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

        writer.WriteStartArray("fields");
        foreach (var field in fields)
        {
            writer.WriteStartObject();
            writer.WriteString("referenceName", field.ReferenceName);
            writer.WriteString("displayName", field.DisplayName);
            writer.WriteString("dataType", field.DataType);
            writer.WriteBoolean("isReadOnly", field.IsReadOnly);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("transitions");
        for (var i = 0; i < type.States.Count; i++)
        {
            for (var j = 0; j < type.States.Count; j++)
            {
                if (i == j) continue;
                var from = type.States[i];
                var to = type.States[j];
                writer.WriteStartObject();
                writer.WriteString("from", from.Name);
                writer.WriteString("to", to.Name);
                writer.WriteString("kind", to.Category == Domain.Enums.StateCategory.Removed
                    ? "Cut" : "Forward");
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();

        writer.WriteEndObject();

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
