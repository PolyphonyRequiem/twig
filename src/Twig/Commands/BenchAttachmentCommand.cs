using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Browser;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

internal sealed class BenchAttachmentCommand(
    CommandContext ctx,
    Func<BrowserAttachmentModule> attachments,
    TextWriter? stdout = null,
    Stream? stdin = null,
    Stream? binaryStdout = null)
{
    internal const int MaximumInputBytes = 262144;
    private static readonly TwigJsonContext StrictJson = new(new JsonSerializerOptions(TwigJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    });

    internal static async Task<AttachmentReadRequest> ReadRequestAsync(Stream input, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumInputBytes) throw new ArgumentException("input-too-large");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(parsed.RootElement);
        return parsed.RootElement.Deserialize(StrictJson.AttachmentReadRequest) ?? throw new JsonException();
    }

    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicates(child);
    }

    internal async Task<int> ExecuteAsync(bool readStdin, string output, bool framed, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(output);
        try
        {
            if (format is not ("human" or "minimal" or "json" or "json-full" or "json-compact"))
            {
                CommandError.Write(new(), ctx.StderrWriter, "json", "Attachments supports human, minimal and JSON output.");
                return exit = 2;
            }

            AttachmentReadRequest? request = null;
            AttachmentReadOutcome outcome;
            try
            {
                if (!readStdin)
                    outcome = BrowserAttachmentModule.Refused("usage");
                else
                {
                    request = await ReadRequestAsync(stdin ?? Console.OpenStandardInput(), ct).ConfigureAwait(false);
                    if (!BrowserAttachmentModule.Valid(request))
                        outcome = BrowserAttachmentModule.Refused("usage");
                    else if (framed != (request.Action == "download") || framed && format != "human")
                        outcome = BrowserAttachmentModule.Refused("usage");
                    else if (ctx.Config.ResolveWorkItemProviderKind() != "ado")
                        outcome = BrowserAttachmentModule.Refused("provider-unsupported", unsupported: true);
                    else
                        outcome = await ReadNativeAsync(request, ct).ConfigureAwait(false);
                }
            }
            catch (JsonException) { outcome = BrowserAttachmentModule.Refused("invalid-json"); }
            catch (ArgumentException ex)
            {
                outcome = BrowserAttachmentModule.Refused(
                    ex.Message == "input-too-large" ? "input-too-large" : "usage");
            }
            catch (TwigConfigurationException ex)
            {
                var code = ex.Message.StartsWith(WorkItemProviderSetting.UnknownReasonCode + ":", StringComparison.Ordinal)
                    ? WorkItemProviderSetting.UnknownReasonCode : WorkItemProviderSetting.UnavailableReasonCode;
                outcome = BrowserAttachmentModule.Refused(code, unsupported: code == WorkItemProviderSetting.UnavailableReasonCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = BrowserAttachmentModule.Refused("canceled");
            }

            if (framed)
            {
                await AttachmentReadFrame.WriteAsync(binaryStdout ?? Console.OpenStandardOutput(),
                    outcome.Result, outcome.Content, ct).ConfigureAwait(false);
            }
            else if (format is "json" or "json-full" or "json-compact")
            {
                (stdout ?? Console.Out).WriteLine(JsonSerializer.Serialize(outcome.Result,
                    TwigJsonContext.Default.AttachmentReadResult));
            }
            else if (outcome.Result.Error is null)
            {
                foreach (var attachment in outcome.Result.Attachments)
                {
                    var name = Quoted(attachment.FileName);
                    (stdout ?? Console.Out).WriteLine(format == "minimal"
                        ? $"{attachment.Id:D}\t{name}\t{attachment.ReportedSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}"
                        : $"Attachment {attachment.Id:D} name={name} size={attachment.ReportedSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
                }
                (stdout ?? Console.Out).WriteLine($"attachments\t{outcome.Result.Attachments.Count}\titem-revision={outcome.Result.Context!.WorkItemRevision}");
            }

            if (outcome.Result.Error is { } error)
                CommandError.Write(new(), ctx.StderrWriter, format, error.Message);
            return exit = outcome.Result.Error?.Code is "usage" or "invalid-json" or "input-too-large"
                ? 2 : outcome.Result.Error is null ? 0 : 1;
        }
        finally { TelemetryHelper.TrackCommand(ctx.TelemetryClient, "bench attachments", framed ? "framed" : format ?? "human", exit, start); }
    }

    private async Task<AttachmentReadOutcome> ReadNativeAsync(AttachmentReadRequest request, CancellationToken ct)
    {
        BrowserAttachmentModule module;
        try { module = attachments(); }
        catch (SqliteException) { return BrowserAttachmentModule.Refused("attachment-unavailable"); }
        catch (AdoException ex)
        {
            return BrowserAttachmentModule.Refused(ex switch
            {
                AdoAuthenticationException => "attachment-denied",
                AdoOfflineException => "attachment-offline",
                AdoRateLimitException => "attachment-throttled",
                _ => "attachment-unavailable",
            });
        }
        return await module.ReadAsync(request, ct).ConfigureAwait(false);
    }

    private static string Quoted(string text)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes)) json.WriteStringValue(text);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
