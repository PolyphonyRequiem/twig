using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Browser;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

internal sealed class BenchAttachmentUploadCommand(
    CommandContext ctx,
    Func<BrowserAttachmentUploadModule> uploads,
    Stream? stdin = null,
    TextWriter? stdout = null)
{
    internal async Task<int> ExecutePrepareAsync(
        bool readStdin, string output, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(output);
        AttachmentUploadResult result;
        try
        {
            if (!ValidOutput(format))
                result = AttachmentUploadFrame.Refused("usage");
            else if (!readStdin)
                result = AttachmentUploadFrame.Refused("usage");
            else
            {
                var request = await AttachmentUploadFrame.ReadPrepareRequestAsync(
                    stdin ?? Console.OpenStandardInput(), ct).ConfigureAwait(false);
                if (!BrowserAttachmentUploadModule.Valid(request))
                    result = AttachmentUploadFrame.Refused("usage");
                else if (ctx.Config.ResolveWorkItemProviderKind() != "ado")
                    result = AttachmentUploadFrame.Refused("provider-unsupported", unsupported: true);
                else
                    result = await PrepareNativeAsync(request, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException) { result = AttachmentUploadFrame.Refused("invalid-json"); }
        catch (InvalidDataException ex)
        {
            result = AttachmentUploadFrame.Refused(ex.Message.Contains("too large", StringComparison.Ordinal)
                ? "input-too-large" : "invalid-frame");
        }
        catch (TwigConfigurationException ex)
        {
            var code = ex.Message.StartsWith(WorkItemProviderSetting.UnknownReasonCode + ":", StringComparison.Ordinal)
                ? WorkItemProviderSetting.UnknownReasonCode : WorkItemProviderSetting.UnavailableReasonCode;
            result = AttachmentUploadFrame.Refused(code, unsupported: code == WorkItemProviderSetting.UnavailableReasonCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = AttachmentUploadFrame.Refused("canceled");
        }

        exit = await WriteResultAsync(result, format, "bench attachments prepare-upload").ConfigureAwait(false);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "bench attachments prepare-upload",
            format ?? "human", exit, started);
        return exit;
    }

    internal async Task<int> ExecuteUploadAsync(
        bool readStdin, bool framed, string output, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(output);
        AttachmentUploadResult result;
        try
        {
            if (!ValidOutput(format) || !readStdin || !framed)
                result = AttachmentUploadFrame.Refused("usage");
            else
            {
                var frame = await AttachmentUploadFrame.ReadAsync(
                    stdin ?? Console.OpenStandardInput(), ct).ConfigureAwait(false);
                if (!BrowserAttachmentUploadModule.Valid(frame.Request))
                    result = AttachmentUploadFrame.Refused("usage");
                else if (ctx.Config.ResolveWorkItemProviderKind() != "ado")
                    result = AttachmentUploadFrame.Refused("provider-unsupported", unsupported: true);
                else
                    result = await UploadNativeAsync(frame.Request, frame.Content, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException) { result = AttachmentUploadFrame.Refused("invalid-json"); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            result = AttachmentUploadFrame.Refused("invalid-frame");
        }
        catch (TwigConfigurationException ex)
        {
            var code = ex.Message.StartsWith(WorkItemProviderSetting.UnknownReasonCode + ":", StringComparison.Ordinal)
                ? WorkItemProviderSetting.UnknownReasonCode : WorkItemProviderSetting.UnavailableReasonCode;
            result = AttachmentUploadFrame.Refused(code, unsupported: code == WorkItemProviderSetting.UnavailableReasonCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = AttachmentUploadFrame.Refused("canceled");
        }

        exit = await WriteResultAsync(result, format, "bench attachments upload").ConfigureAwait(false);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "bench attachments upload",
            format ?? "human", exit, started);
        return exit;
    }

    private async Task<AttachmentUploadResult> PrepareNativeAsync(
        AttachmentUploadPrepareRequest request, CancellationToken ct)
    {
        BrowserAttachmentUploadModule module;
        try { module = uploads(); }
        catch (SqliteException) { return AttachmentUploadFrame.Refused("attachment-unavailable"); }
        catch (AdoException ex) { return AttachmentUploadFrame.Refused(ToAttachmentCode(ex)); }
        return await module.PrepareAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<AttachmentUploadResult> UploadNativeAsync(
        AttachmentUploadRequest request, byte[] content, CancellationToken ct)
    {
        BrowserAttachmentUploadModule module;
        try { module = uploads(); }
        catch (SqliteException) { return AttachmentUploadFrame.Refused("attachment-unavailable"); }
        catch (AdoException ex) { return AttachmentUploadFrame.Refused(ToAttachmentCode(ex)); }
        return await module.UploadAsync(request, content, ct).ConfigureAwait(false);
    }

    private async Task<int> WriteResultAsync(AttachmentUploadResult result, string? format, string command)
    {
        if (format is "json" or "json-full" or "json-compact")
            (stdout ?? Console.Out).WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.AttachmentUploadResult));
        else if (result.Error is null)
        {
            var name = JsonSerializer.Serialize(result.FileName ?? string.Empty, TwigJsonContext.Default.String);
            var line = result.Status switch
            {
                "prepared" => $"Prepared upload for {name} ({result.ContentLength} bytes); intent={result.IntentDigest}. No upload was performed.",
                "uploaded" => $"Uploaded and attached {name} to work item #{result.Context!.ItemKey.WorkItemId} at revision {result.NewWorkItemRevision}.",
                _ => result.Status,
            };
            (stdout ?? Console.Out).WriteLine(format == "minimal"
                ? $"{result.Status}\t{result.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}\t{result.IntentDigest ?? "null"}"
                : line);
        }

        if (result.Error is { } error)
            CommandError.Write(new(), ctx.StderrWriter, format ?? "human", error.Message);
        return result.Status is "prepared" or "uploaded" ? 0
            : result.Error?.Code is "usage" or "invalid-json" or "invalid-frame" or "input-too-large" ? 2 : 1;
    }

    private static bool ValidOutput(string? format) =>
        format is "human" or "minimal" or "json" or "json-full" or "json-compact";

    private static string ToAttachmentCode(AdoException exception) => exception switch
    {
        AdoAuthenticationException => "attachment-denied",
        AdoNotFoundException => "attachment-not-found",
        AdoOfflineException => "attachment-offline",
        AdoRateLimitException => "attachment-throttled",
        _ => "attachment-unavailable",
    };
}
