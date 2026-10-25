using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Browser;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

internal sealed class BenchDiscussionCommand(
    CommandContext ctx, Func<BrowserDiscussionModule> discussion, TextWriter? stdout = null, Stream? stdin = null)
{
    internal const int MaximumInputBytes = 262144;
    private static readonly TwigJsonContext StrictJson = new(new JsonSerializerOptions(TwigJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    });

    internal static async Task<DiscussionRequest> ReadRequestAsync(Stream input, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (bytes.Length + count > MaximumInputBytes) throw new ArgumentException("input-too-large");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(parsed.RootElement);
        return parsed.RootElement.Deserialize(StrictJson.DiscussionRequest) ?? throw new JsonException();
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

    internal async Task<int> ExecuteAsync(bool readStdin, string output, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(output);
        try
        {
            if (format is not ("human" or "minimal" or "json" or "json-full" or "json-compact"))
            {
                CommandError.Write(new(), ctx.StderrWriter, "json", "Discussion supports human, minimal and JSON, not ids.");
                return exit = 2;
            }
            DiscussionResult result;
            try
            {
                if (!readStdin) result = BrowserDiscussionModule.Refused("usage");
                else
                {
                    var request = await ReadRequestAsync(stdin ?? Console.OpenStandardInput(), ct);
                    if (!BrowserDiscussionModule.Valid(request)) result = BrowserDiscussionModule.Refused("usage");
                    else if (ctx.Config.ResolveWorkItemProviderKind() != "ado")
                        result = BrowserDiscussionModule.Refused("provider-unsupported");
                    else result = await ReadNativeAsync(request, ct);
                }
            }
            catch (JsonException) { result = BrowserDiscussionModule.Refused("invalid-json"); }
            catch (ArgumentException ex) { result = BrowserDiscussionModule.Refused(ex.Message == "input-too-large" ? "input-too-large" : "usage"); }
            catch (TwigConfigurationException ex)
            {
                result = BrowserDiscussionModule.Refused(
                    ex.Message.StartsWith(WorkItemProviderSetting.UnknownReasonCode + ":", StringComparison.Ordinal)
                        ? WorkItemProviderSetting.UnknownReasonCode : WorkItemProviderSetting.UnavailableReasonCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = BrowserDiscussionModule.Refused("canceled"); }
            var writer = stdout ?? Console.Out;
            if (format is "json" or "json-full" or "json-compact")
                writer.WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.DiscussionResult));
            else if (result.Error is null)
            {
                foreach (var comment in result.Comments)
                {
                    var cells = new[]
                    {
                        Quoted(comment.Id), Quoted(comment.Version), Quoted(comment.Author.Id),
                        Quoted(comment.Author.DisplayName), Quoted(comment.CreatedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                        Quoted(comment.ModifiedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                        comment.IsDeleted is { } deleted ? deleted ? "true" : "false" : "null",
                        Quoted(comment.Format), Quoted(comment.Body),
                    };
                    writer.WriteLine(format == "minimal" ? string.Join('\t', cells)
                        : $"Comment {cells[0]} version={cells[1]} authorId={cells[2]} author={cells[3]} createdAt={cells[4]} modifiedAt={cells[5]} deleted={cells[6]} format={cells[7]} body={cells[8]}");
                }
                writer.WriteLine($"discussion\t{result.Paging!.Count}\t{result.Paging.Traversal}\tsnapshot-not-guaranteed");
            }
            if (result.Error is { } error) CommandError.Write(new(), ctx.StderrWriter, format, error.Message);
            return exit = result.Error?.Code is "usage" or "invalid-json" or "input-too-large" ? 2 : result.Error is null ? 0 : 1;
        }
        finally { TelemetryHelper.TrackCommand(ctx.TelemetryClient, "bench discussion", format ?? "human", exit, start); }
    }

    private async Task<DiscussionResult> ReadNativeAsync(DiscussionRequest request, CancellationToken ct)
    {
        BrowserDiscussionModule module;
        try { module = discussion(); }
        catch (SqliteException) { return BrowserDiscussionModule.Refused("discussion-unavailable"); }
        catch (AdoException ex)
        {
            return BrowserDiscussionModule.Refused(ex switch
            {
                AdoAuthenticationException => "discussion-denied",
                AdoOfflineException => "discussion-offline",
                AdoRateLimitException => "discussion-throttled",
                _ => "discussion-unavailable",
            });
        }
        return await module.ReadAsync(request, ct);
    }

    private static string Quoted(string? text)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes))
        {
            if (text is null) json.WriteNullValue();
            else json.WriteStringValue(text);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
