using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Browser;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

internal sealed class BenchDiscussionPostCommand(
    CommandContext ctx, Func<BrowserDiscussionPostModule> module, Stream? stdin = null)
{
    private static readonly TwigJsonContext StrictJson = new(new JsonSerializerOptions(TwigJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32,
    });

    internal async Task<int> ExecuteAsync(bool readStdin, string output, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(output);
        try
        {
            if (format is not ("human" or "minimal" or "json" or "json-full" or "json-compact"))
            {
                CommandError.Write(new(), ctx.StderrWriter, "json", "Discussion preparation supports human, minimal and JSON, not ids.");
                return exit = 2;
            }
            DiscussionPostPreparation result;
            try
            {
                if (!readStdin) result = BrowserDiscussionPostModule.Refused("usage");
                else
                {
                    using var bytes = new MemoryStream();
                    var input = stdin ?? Console.OpenStandardInput();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        if (bytes.Length + count > BenchDiscussionCommand.MaximumInputBytes)
                            throw new ArgumentException("input-too-large");
                        await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                    RejectDuplicates(document.RootElement);
                    var request = document.RootElement.Deserialize(StrictJson.DiscussionPostRequest) ?? throw new JsonException();
                    result = ctx.Config.ResolveWorkItemProviderKind() != "ado"
                        ? BrowserDiscussionPostModule.Refused("provider-unsupported")
                        : await module().PrepareAsync(request, ct);
                }
            }
            catch (JsonException) { result = BrowserDiscussionPostModule.Refused("invalid-json"); }
            catch (ArgumentException ex) { result = BrowserDiscussionPostModule.Refused(ex.Message == "input-too-large" ? ex.Message : "usage"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = BrowserDiscussionPostModule.Refused("canceled"); }
            catch (TwigConfigurationException ex)
            {
                result = BrowserDiscussionPostModule.Refused(
                    ex.Message.StartsWith(WorkItemProviderSetting.UnknownReasonCode + ":", StringComparison.Ordinal)
                        ? WorkItemProviderSetting.UnknownReasonCode : WorkItemProviderSetting.UnavailableReasonCode);
            }
            catch (SqliteException) { result = BrowserDiscussionPostModule.Refused("discussion-prepare-unavailable"); }
            catch (AdoException) { result = BrowserDiscussionPostModule.Refused("discussion-prepare-unavailable"); }
            catch (InvalidOperationException) { result = BrowserDiscussionPostModule.Refused("discussion-prepare-unavailable"); }
            if (format is "json" or "json-full" or "json-compact")
                Console.Out.WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.DiscussionPostPreparation));
            else if (result.Status == "prepared")
            {
                Console.Out.WriteLine($"digest\t{result.Digest}");
                Console.Out.WriteLine($"proposal\t{result.Proposal}");
                if (format == "human") Console.Out.WriteLine("Prepared only. Review and authorize the exact digest separately; no comment was posted.");
            }
            if (result.Error is { } error) CommandError.Write(new(), ctx.StderrWriter, format, error.Message);
            return exit = result.Error?.Code is "usage" or "invalid-json" or "input-too-large" ? 2 : result.Error is null ? 0 : 1;
        }
        finally { TelemetryHelper.TrackCommand(ctx.TelemetryClient, "bench discussion prepare-post", format ?? "human", exit, started); }
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
            foreach (var item in node.EnumerateArray()) RejectDuplicates(item);
    }
}
