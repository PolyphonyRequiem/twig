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
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(outputFormat, ct).ConfigureAwait(false);
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

    private async Task<int> ExecuteCoreAsync(string outputFormat, CancellationToken ct)
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

        CapabilityReport report;
        try
        {
            var evidence = await provider.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
            report = CapabilityReport.Create(provider.Capabilities, evidence);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(fmt.FormatError($"Capability report refused: {ex.Message}"));
            return 1;
        }

        Render(report, outputFormat);
        return 0;
    }

    private void Render(CapabilityReport report, string outputFormat)
    {
        var normalized = OutputFormats.Normalize(outputFormat);
        if (normalized is "json" or "json-full" or "json-compact")
        {
            Console.Out.WriteLine(ToJson(report));
            return;
        }

        var nodes = new List<RenderNode>();
        if (normalized is "minimal")
        {
            foreach (var entry in report.Relationships)
                nodes.Add(new RenderNode.Text(string.Join('\t', Cells(entry))));
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

    internal static string ToJson(CapabilityReport report)
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
