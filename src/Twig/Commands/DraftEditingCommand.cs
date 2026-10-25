using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Twig.Domain.Services.Plan;
using Twig.Browser;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

internal sealed class DraftEditingCommand(CommandContext ctx, Func<RecoverableDraftEditingModule> editing,
    TextWriter? stdout = null, Stream? stdin = null)
{
    internal const int MaximumInputBytes = 256 * 1024;
    private static readonly TwigJsonContext StrictInputJson = new(new JsonSerializerOptions(TwigJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    });

    internal Task<int> OpenAsync(int id, string bench, string output = OutputFormatterFactory.DefaultFormat,
        string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => ExecuteAsync("draft open", output,
            () => id <= 0 || string.IsNullOrWhiteSpace(bench)
                ? Task.FromResult(RecoverableDraftEditingModule.Refused("usage",
                    "A positive native ADO item ID and explicit saved Bench are required."))
                : NativeAsync(module => module.OpenAsync(id, bench, expectBinding, expectIdentity, ct)), ct);

    internal Task<int> StageAsync(bool readStdin, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => ExecuteAsync("draft stage", output, async () =>
        {
            if (!readStdin)
                return RecoverableDraftEditingModule.Refused("usage", "Use --stdin and close stdin after one version 1 JSON request.");
            var request = await ReadRequestAsync(stdin ?? Console.OpenStandardInput(), ct);
            if (!RecoverableDraftEditingModule.IsValidRequest(request))
                return RecoverableDraftEditingModule.Refused("usage",
                    "Version 1, exact native context, canonical decimal tokens and 1-128 ordered changes are required.");
            return await NativeAsync(module => module.StageAsync(request, ct));
        }, ct);

    internal Task<int> RetainAsync(bool readStdin, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => ExecuteAsync("draft retain", output, async () =>
        {
            if (!readStdin)
                return RecoverableDraftEditingModule.Refused("usage", "Use --stdin and close stdin after one version 1 retain request.");
            var request = await ReadRetainRequestAsync(stdin ?? Console.OpenStandardInput(), ct);
            if (!RecoverableDraftEditingModule.IsValidRequest(request))
                return RecoverableDraftEditingModule.Refused("usage", "Exact native context and canonical decimal tokens are required.");
            return await NativeAsync(module => module.RetainAsync(request,
                new PlanWorkspace { Organization = ctx.Config.Organization, Project = ctx.Config.Project }, ct));
        }, ct);

    private async Task<DraftEditingResult> NativeAsync(Func<RecoverableDraftEditingModule, Task<DraftEditingResult>> action)
    {
        try
        {
            if (ctx.Config.ResolveWorkItemProviderKind() != "ado")
                return RecoverableDraftEditingModule.Refused("provider-unsupported",
                    "This native form editor supports numeric ADO items only; cross-provider editing is unavailable.");
            return await action(editing());
        }
        catch (TwigConfigurationException ex)
        {
            var code = ex.Message.StartsWith(WorkItemProviderSetting.UnknownReasonCode + ":", StringComparison.Ordinal)
                ? WorkItemProviderSetting.UnknownReasonCode
                : ex.Message.StartsWith(WorkItemProviderSetting.UnconfiguredReasonCode + ":", StringComparison.Ordinal)
                    ? WorkItemProviderSetting.UnconfiguredReasonCode : WorkItemProviderSetting.UnavailableReasonCode;
            return RecoverableDraftEditingModule.Refused(code, ex.Message);
        }
    }

    internal static Task<DraftStageRequest> ReadRequestAsync(Stream input, CancellationToken ct)
        => ReadRequestAsync(input, StrictInputJson.DraftStageRequest, ct);

    internal static Task<DraftRetainRequest> ReadRetainRequestAsync(Stream input, CancellationToken ct)
        => ReadRequestAsync(input, StrictInputJson.DraftRetainRequest, ct);

    private static async Task<T> ReadRequestAsync<T>(Stream input, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (bytes.Length + count > MaximumInputBytes)
                throw new ArgumentException("input-too-large: draft stdin exceeds the 262144-byte UTF-8 limit; nothing was committed.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(parsed.RootElement);
        return parsed.RootElement.Deserialize(type)
            ?? throw new JsonException("A version 1 draft request is required.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException("Duplicate JSON properties are not accepted.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                RejectDuplicateProperties(child);
    }

    private async Task<int> ExecuteAsync(string command, string output,
        Func<Task<DraftEditingResult>> action, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(output);
        var exit = 1;
        try
        {
            if (format is not ("human" or "minimal" or "json" or "json-full" or "json-compact"))
            {
                CommandError.Write(new(), ctx.StderrWriter, "json",
                    "Draft editing supports human, minimal and json output, not ids.");
                return exit = 2;
            }
            DraftEditingResult result;
            try { result = await action(); }
            catch (JsonException ex) { result = RecoverableDraftEditingModule.Refused("invalid-json", ex.Message); }
            catch (ArgumentException ex) { result = RecoverableDraftEditingModule.Refused(
                ex.Message.StartsWith("input-too-large:", StringComparison.Ordinal) ? "input-too-large" : "usage", ex.Message); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { result = RecoverableDraftEditingModule.Refused("canceled", "Draft editing was canceled."); }
            if (format is "json" or "json-full" or "json-compact")
            {
                (stdout ?? Console.Out).WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.DraftEditingResult));
                if (result.Error is { } jsonError)
                    CommandError.Write(new(), ctx.StderrWriter, format, jsonError.Message);
            }
            else if (result.Error is { } error)
                ctx.StderrWriter.WriteLine(ctx.FormatterFactory.GetFormatter(format).FormatError(error.Message));
            else if (result.Capture is { } retained)
                (stdout ?? Console.Out).WriteLine($"draft retained\t{retained.Digest}\t{retained.Generation}");
            else
                (stdout ?? Console.Out).WriteLine(
                    $"draft {result.Status}\t{result.Document!.NativeContext.ItemKey.WorkItemId}\t{result.Document.Generation}");
            exit = result.Error?.Code is "usage" or "invalid-json" or "input-too-large" ? 2
                : result.Error is null ? 0 : 1;
            return exit;
        }
        finally { TelemetryHelper.TrackCommand(ctx.TelemetryClient, command, format ?? "human", exit, started); }
    }
}
