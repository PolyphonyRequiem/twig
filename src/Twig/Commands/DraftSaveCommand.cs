using System.Diagnostics;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Formatters;
using Twig.Infrastructure.Plan;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Consumes an explicitly saved retained capture, never a replacement proposal file.</summary>
internal sealed class DraftSaveCommand(CommandContext ctx, IPlanLifecycleService lifecycle,
    IPlanOriginProvider origins, TextWriter? stdout = null, Stream? stdin = null)
{
    internal async Task<int> ExecuteAsync(bool readStdin,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(output);
        var exit = 1;
        try
        {
            if (format is not ("human" or "minimal" or "json" or "json-full" or "json-compact"))
            {
                CommandError.Write(new RendererFactory(), ctx.StderrWriter, "json",
                    "Draft Save supports human, minimal and json output.");
                return exit = 2;
            }
            if (!readStdin)
                throw new ArgumentException("Save requires one explicit workbench Save request through --stdin.");
            var request = await DraftEditingCommand.ReadSaveRequestAsync(stdin ?? Console.OpenStandardInput(), ct);
            var digest = request.Digest;
            if (request.Version != 1 || digest is not { Length: 64 }
                || !digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                || string.IsNullOrWhiteSpace(request.CallerIdentity)
                || request.Surface != "twig-workbench-explicit-save")
                throw new ArgumentException("Save requires the exact retained digest, captured caller identity and explicit workbench Save surface.");
            if (lifecycle is not PlanLifecycleService native)
                throw new InvalidOperationException("Native captured-draft consumption is unavailable.");
            var origin = await origins.GetOriginAsync(ct);
            if (request.CallerIdentity != origin.IdentityId)
                throw new InvalidOperationException("Save caller differs from the admitted native identity; no identity was selected.");

            var result = await native.ApplyCapturedDraftAsync(digest, ct);
            var verified = !result.Failed && result.Error is null && result.Operations.Count > 0
                && result.Operations.All(row => row.State == PlanOperationState.Verified)
                && result.UnsettledAcknowledgments.Count == 0;
            var settled = verified && result.DraftSettlement == "settled" && result.DraftSettlementError is null;
            var writer = stdout ?? Console.Out;
            if (format is "json" or "json-full" or "json-compact")
            {
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer))
                {
                    json.WriteStartObject();
                    json.WriteNumber("version", 1);
                    json.WriteString("digest", result.Digest);
                    json.WriteString("status", settled ? "saved" : verified ? "verified-draft-retained" : "not-verified");
                    json.WriteBoolean("verified", verified);
                    json.WriteBoolean("settled", settled);
                    json.WriteString("callerIdentity", origin.IdentityId);
                    json.WriteString("surface", request.Surface);
                    json.WriteBoolean("hostAttested", false);
                    json.WriteString("error", result.Error);
                    json.WriteString("draftSettlement", result.DraftSettlement);
                    json.WriteString("draftSettlementError", result.DraftSettlementError);
                    json.WriteStartArray("operations");
                    foreach (var row in result.Operations)
                    {
                        json.WriteStartObject();
                        json.WriteString("opId", row.OpId);
                        json.WriteNumber("ordinal", row.Ordinal);
                        json.WriteString("state", row.State.ToString());
                        json.WriteString("error", row.Error);
                        json.WriteString("warning", row.Warning);
                        json.WriteString("resultJson", row.ResultJson);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteStartObject("unsettledAcknowledgments");
                    foreach (var acknowledgment in result.UnsettledAcknowledgments)
                        json.WriteString(acknowledgment.Key, acknowledgment.Value);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                writer.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            }
            else
                writer.WriteLine($"{(settled ? "saved" : verified ? "verified-draft-retained" : "not-verified")}\t{result.Digest}");
            if (!settled)
                CommandError.Write(new RendererFactory(), ctx.StderrWriter, format,
                    result.Error ?? result.DraftSettlementError
                    ?? "Save is not fully verified and settled. Inspect the native journal; do not replay uncertain effects.");
            return exit = settled ? 0 : 1;
        }
        catch (ArgumentException ex)
        {
            CommandError.Write(new RendererFactory(), ctx.StderrWriter, format ?? "human", ex.Message);
            return exit = 2;
        }
        catch (JsonException ex)
        {
            CommandError.Write(new RendererFactory(), ctx.StderrWriter, format ?? "human", ex.Message);
            return exit = 2;
        }
        catch (InvalidOperationException ex)
        {
            CommandError.Write(new RendererFactory(), ctx.StderrWriter, format ?? "human", ex.Message);
            return exit;
        }
        finally { TelemetryHelper.TrackCommand(ctx.TelemetryClient, "draft save", format ?? "human", exit, started); }
    }
}
