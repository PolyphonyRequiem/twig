using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig connection capabilities</c> (AB#1170): a read-only report of how the
/// active provider implements each Twig relationship kind, combined with its runtime
/// availability evidence.
/// <para>
/// Everything is derived from the active <see cref="IWorkItemProvider"/>: its static
/// <see cref="IWorkItemProvider.Capabilities"/> declaration and whatever
/// <see cref="IWorkItemProvider.ProbeCapabilitiesAsync"/> returns. Kinds without evidence are
/// reported <c>unknown</c> / <c>not-probed</c>, never as unsupported. The command enables no
/// emulation and writes nothing.
/// </para>
/// <para>
/// Exit codes: <c>0</c> when the report was produced; <c>1</c> when no provider is resolvable,
/// the probe fails, or the provider returns evidence its own descriptor does not declare.
/// </para>
/// </summary>
internal sealed class ConnectionCapabilitiesCommand(
    Func<IWorkItemProvider?> providerResolver,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null,
    ITelemetryClient? telemetryClient = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool includeGitHubSchema = false,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(outputFormat, includeGitHubSchema, ct).ConfigureAwait(false);
        try
        {
            TelemetryHelper.TrackCommand(telemetryClient, "connection capabilities",
                OutputFormats.Normalize(outputFormat) ?? OutputFormatterFactory.DefaultFormat, exitCode, startTimestamp);
        }
        catch (Exception)
        {
            Trace.TraceWarning("Optional command telemetry was unavailable.");
        }

        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(string outputFormat, bool includeGitHubSchema, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        IWorkItemProvider? provider;
        try
        {
            provider = providerResolver();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(fmt.FormatError($"No active provider could be resolved: {ex.Message}"));
            return 1;
        }

        if (provider is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active provider is registered; capabilities are unknown."));
            return 1;
        }

        if (includeGitHubSchema
            && provider.Capabilities.ProviderKind != ProviderCapabilityCatalog.GitHubProviderKind)
        {
            CommandError.Write(_rendererFactory, Console.Error, outputFormat,
                "The --github-schema option requires the active GitHub provider. ADO process metadata remains available through 'twig process'.");
            return 2;
        }

        CapabilityReport report;
        ProviderSchemaMetadata? schema = null;
        try
        {
            var evidence = await provider.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
            report = CapabilityReport.Create(provider.Capabilities, evidence);
            if (includeGitHubSchema)
            {
                if (provider is not IProviderSchemaMetadataReader schemaReader)
                    throw new InvalidOperationException("The GitHub provider does not expose schema metadata.");
                schema = await schemaReader.DiscoverSchemaMetadataAsync(ct).ConfigureAwait(false);
                if (schema.ProviderKind != provider.Capabilities.ProviderKind)
                    throw new InvalidDataException("Schema metadata does not match the active provider.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(fmt.FormatError($"Capability report refused: {ex.Message}"));
            return 1;
        }

        Render(report, outputFormat, schema);
        return 0;
    }

    private void Render(CapabilityReport report, string outputFormat, ProviderSchemaMetadata? schema)
    {
        var normalized = OutputFormats.Normalize(outputFormat);
        if (normalized is "json" or "json-full" or "json-compact")
        {
            Console.Out.WriteLine(ToJson(report, schema));
            return;
        }

        var nodes = new List<RenderNode>();
        if (normalized is "minimal")
        {
            foreach (var entry in report.Relationships)
                nodes.Add(new RenderNode.Text(string.Join('\t', Cells(entry))));
            if (schema is not null)
            {
                foreach (var row in SchemaRows(schema))
                    nodes.Add(new RenderNode.Text(string.Join('\t', row)));
            }
        }
        else
        {
            nodes.Add(new RenderNode.Text($"Provider capabilities: {report.ProviderKind}"));
            foreach (var entry in report.Relationships)
            {
                var cells = Cells(entry);
                var storage = entry.Storage == EmulationStorage.None ? string.Empty : $" storage={cells[2]}";
                var reason = entry.ReasonCode is null ? string.Empty : $" ({entry.ReasonCode})";
                nodes.Add(new RenderNode.Text($"  {cells[0],-22} {cells[1],-12}{storage} availability={cells[3]}{reason}"));
            }
            if (schema is not null)
            {
                nodes.Add(new RenderNode.Text($"GitHub schema metadata: {SchemaAvailabilityToken(schema.Availability)}"
                    + (schema.ReasonCode is null ? string.Empty : $" ({schema.ReasonCode})")));
                foreach (var item in schema.Types)
                    nodes.Add(new RenderNode.Text($"  type {item.Name}: {SchemaSupportToken(item.Support)}"
                        + (item.ReasonCode is null ? string.Empty : $" ({item.ReasonCode})")));
                nodes.Add(new RenderNode.Text($"  states {schema.States.Name}: {SchemaSupportToken(schema.States.Support)}"
                    + (schema.States.Values.Count == 0 ? string.Empty : $" [{string.Join(", ", schema.States.Values)}]")
                    + (schema.States.ReasonCode is null ? string.Empty : $" ({schema.States.ReasonCode})")));
                nodes.Add(new RenderNode.Text("  fields:"));
                foreach (var field in schema.Fields)
                    nodes.Add(new RenderNode.Text($"    {field.Name}: {field.TypeName ?? "unknown"} "
                        + SchemaSupportToken(field.Support)
                        + (field.ReasonCode is null ? string.Empty : $" ({field.ReasonCode})")));
                nodes.Add(new RenderNode.Text("  schema relationships:"));
                foreach (var relationship in schema.Relationships)
                    nodes.Add(new RenderNode.Text($"    {LinkKindTokens.ToToken(relationship.Kind)}: "
                        + $"{SchemaSupportToken(relationship.Support)} fields=[{string.Join(", ", relationship.Fields)}]"
                        + (relationship.ReasonCode is null ? string.Empty : $" ({relationship.ReasonCode})")));
                nodes.Add(new RenderNode.Text($"  process rules: {SchemaSupportToken(schema.Rules.Process)} "
                    + $"({schema.Rules.ProcessReasonCode}); field rules: {SchemaSupportToken(schema.Rules.Fields)} "
                    + $"({schema.Rules.FieldsReasonCode})"));
            }
        }

        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(nodes));
    }

    private static string[] Cells(CapabilityReportEntry entry) =>
    [
        LinkKindTokens.ToToken(entry.Kind),
        CapabilityTokens.ToToken(CapabilityTokens.Implementation, entry.Implementation),
        CapabilityTokens.ToToken(CapabilityTokens.Storage, entry.Storage),
        CapabilityTokens.ToToken(CapabilityTokens.Availability, entry.Availability),
        entry.ReasonCode ?? string.Empty,
    ];

    internal static string ToJson(CapabilityReport report, ProviderSchemaMetadata? schema = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", report.ProviderKind);
            writer.WriteStartArray("relationships");
            foreach (var entry in report.Relationships)
            {
                var cells = Cells(entry);
                writer.WriteStartObject();
                writer.WriteString("kind", cells[0]);
                writer.WriteString("implementation", cells[1]);
                writer.WriteString("storage", cells[2]);
                writer.WriteString("availability", cells[3]);
                if (entry.ReasonCode is null)
                    writer.WriteNull("reason");
                else
                    writer.WriteString("reason", entry.ReasonCode);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (schema is not null)
                WriteSchema(writer, schema);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSchema(Utf8JsonWriter writer, ProviderSchemaMetadata schema)
    {
        writer.WriteStartObject("schema");
        writer.WriteString("availability", SchemaAvailabilityToken(schema.Availability));
        WriteNullableString(writer, "reason", schema.ReasonCode);
        writer.WriteStartArray("types");
        foreach (var item in schema.Types)
        {
            writer.WriteStartObject();
            writer.WriteString("name", item.Name);
            writer.WriteString("support", SchemaSupportToken(item.Support));
            WriteNullableString(writer, "reason", item.ReasonCode);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("fields");
        foreach (var item in schema.Fields)
        {
            writer.WriteStartObject();
            writer.WriteString("name", item.Name);
            WriteNullableString(writer, "type", item.TypeName);
            writer.WriteString("support", SchemaSupportToken(item.Support));
            WriteNullableString(writer, "reason", item.ReasonCode);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("states");
        writer.WriteString("name", schema.States.Name);
        writer.WriteString("support", SchemaSupportToken(schema.States.Support));
        writer.WriteStartArray("values");
        foreach (var value in schema.States.Values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
        WriteNullableString(writer, "reason", schema.States.ReasonCode);
        writer.WriteEndObject();
        writer.WriteStartArray("relationships");
        foreach (var item in schema.Relationships)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", LinkKindTokens.ToToken(item.Kind));
            writer.WriteString("support", SchemaSupportToken(item.Support));
            writer.WriteStartArray("fields");
            foreach (var field in item.Fields)
                writer.WriteStringValue(field);
            writer.WriteEndArray();
            WriteNullableString(writer, "reason", item.ReasonCode);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("rules");
        writer.WriteStartObject("process");
        writer.WriteString("support", SchemaSupportToken(schema.Rules.Process));
        writer.WriteString("reason", schema.Rules.ProcessReasonCode);
        writer.WriteEndObject();
        writer.WriteStartObject("fields");
        writer.WriteString("support", SchemaSupportToken(schema.Rules.Fields));
        writer.WriteString("reason", schema.Rules.FieldsReasonCode);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string[]> SchemaRows(ProviderSchemaMetadata schema)
    {
        var rows = new List<string[]>
        {
            new[] { "schema", schema.ProviderKind, string.Empty, SchemaAvailabilityToken(schema.Availability), schema.ReasonCode ?? string.Empty },
        };
        rows.AddRange(schema.Types.Select(item =>
            new[] { "type", item.Name, string.Empty, SchemaSupportToken(item.Support), item.ReasonCode ?? string.Empty }));
        rows.AddRange(schema.Fields.Select(item =>
            new[] { "field", item.Name, item.TypeName ?? string.Empty, SchemaSupportToken(item.Support), item.ReasonCode ?? string.Empty }));
        rows.Add(new[] { "state", schema.States.Name, string.Join(",", schema.States.Values),
            SchemaSupportToken(schema.States.Support), schema.States.ReasonCode ?? string.Empty });
        rows.AddRange(schema.Relationships.Select(item =>
            new[] { "schema-relationship", LinkKindTokens.ToToken(item.Kind), string.Join(",", item.Fields),
                SchemaSupportToken(item.Support), item.ReasonCode ?? string.Empty }));
        rows.Add(new[] { "rule", "process", string.Empty, SchemaSupportToken(schema.Rules.Process), schema.Rules.ProcessReasonCode });
        rows.Add(new[] { "rule", "fields", string.Empty, SchemaSupportToken(schema.Rules.Fields), schema.Rules.FieldsReasonCode });
        return rows;
    }

    private static string SchemaAvailabilityToken(ProviderSchemaDiscoveryAvailability availability) =>
        availability switch
        {
            ProviderSchemaDiscoveryAvailability.Available => "available",
            ProviderSchemaDiscoveryAvailability.Unavailable => "unavailable",
            ProviderSchemaDiscoveryAvailability.Unknown => "unknown",
            _ => throw new UnreachableException($"Unhandled schema availability: {availability}"),
        };

    private static string SchemaSupportToken(ProviderSchemaElementSupport support) =>
        support switch
        {
            ProviderSchemaElementSupport.Supported => "supported",
            ProviderSchemaElementSupport.Unsupported => "unsupported",
            ProviderSchemaElementSupport.Unknown => "unknown",
            _ => throw new UnreachableException($"Unhandled schema support: {support}"),
        };

    private static void WriteNullableString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
            writer.WriteNull(property);
        else
            writer.WriteString(property, value);
    }
}
