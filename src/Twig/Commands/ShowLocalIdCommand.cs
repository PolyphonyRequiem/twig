using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Serialization;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Reads a canonical local work-item identity through the active provider without resolving
/// the cache-first ShowCommand's process, synchronization, tree, or git dependencies.
/// </summary>
public sealed class ShowLocalIdCommand(
    IProviderWorkItemReadService? providerWorkItemReadService,
    RendererFactory rendererFactory,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderrWriter = null)
{
    private readonly TextWriter _stderrWriter = stderrWriter ?? Console.Error;

    public async Task<int> ExecuteAsync(
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool tree = false,
        bool refresh = false,
        CancellationToken ct = default,
        string? fields = null,
        string? sections = null,
        string? localIdText = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        int exitCode;

        if (localIdText is null)
        {
            CommandError.Write(rendererFactory, _stderrWriter, outputFormat, "--local-id requires a value.");
            exitCode = 2;
        }
        else if (id.HasValue || tree || refresh || fields is not null || sections is not null
            || OutputFormats.Normalize(outputFormat) is not ("json" or "json-full" or "json-compact"))
        {
            CommandError.Write(rendererFactory, _stderrWriter, outputFormat,
                "--local-id requires JSON output and cannot be combined with an integer ID, --tree, --refresh, --fields, or --sections.");
            exitCode = 2;
        }
        else if (!LocalWorkItemId.TryParse(localIdText, out var localId))
        {
            CommandError.Write(rendererFactory, _stderrWriter, outputFormat,
                "--local-id must be a canonical lowercase GUIDv7 identity.");
            exitCode = 2;
        }
        else if (providerWorkItemReadService is null)
        {
            CommandError.Write(rendererFactory, _stderrWriter, outputFormat,
                "Provider snapshot reads are not available in this composition.");
            exitCode = 1;
        }
        else
        {
            ProviderWorkItemReadResult? result;
            try
            {
                result = await providerWorkItemReadService.ReadByLocalIdAsync(localId, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                CommandError.Write(rendererFactory, _stderrWriter, outputFormat,
                    $"Could not read local work item {localId}: {ex.Message}");
                exitCode = 1;
                TelemetryHelper.TrackCommand(telemetryClient, "show", outputFormat, exitCode, startTimestamp);
                return exitCode;
            }

            if (result is null)
            {
                CommandError.Write(rendererFactory, _stderrWriter, outputFormat,
                    $"Local work item {localId} was not found or has no identity in the active provider scope.");
                exitCode = 1;
            }
            else
            {
                WriteCanonicalRead(result);
                exitCode = 0;
            }
        }

        TelemetryHelper.TrackCommand(telemetryClient, "show", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private static void WriteCanonicalRead(ProviderWorkItemReadResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("localId", result.Local.Id.ToString());
            writer.WriteNumber("localRevision", result.Local.Revision);
            writer.WriteBoolean("localDeleted", result.Local.IsDeleted);
            writer.WritePropertyName("providerIdentity");
            WriteIdentity(writer, result.Identity);
            if (result.RevisionToken is null)
                writer.WriteNull("providerRevision");
            else
                writer.WriteString("providerRevision", result.RevisionToken.Value);
            writer.WritePropertyName("providerFields");
            JsonSerializer.Serialize(writer, result.ProviderFields, TwigJsonContext.Default.FieldValues);
            writer.WritePropertyName("fields");
            JsonSerializer.Serialize(writer, result.Fields, TwigJsonContext.Default.FieldValues);
            writer.WriteStartArray("relationships");
            foreach (var relationship in result.Relationships)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", LinkKindNames.ToStableName(relationship.Kind));
                writer.WritePropertyName("source");
                WriteIdentity(writer, relationship.Source);
                writer.WritePropertyName("target");
                WriteIdentity(writer, relationship.Target);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Console.Out.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteIdentity(Utf8JsonWriter writer, ExternalWorkItemIdentity identity)
    {
        writer.WriteStartObject();
        writer.WriteString("providerKind", identity.ProviderKind);
        writer.WriteString("resourceScope", identity.ResourceScope);
        writer.WriteString("nativeId", identity.NativeId);
        writer.WriteEndObject();
    }
}
