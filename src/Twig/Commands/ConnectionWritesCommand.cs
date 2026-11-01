using Twig.Infrastructure.Telemetry;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Twig.Infrastructure.Serialization;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Inspect original native uncertain-write evidence without reconciliation.</summary>
internal sealed class ConnectionWritesCommand(
    IConnectionRemoteWriteReconciliationService reconciliation,
    TwigConfiguration configuration,
    TwigPaths paths,
    OutputFormatterFactory formatters,
    RendererFactory renderers,
    ITelemetryClient? telemetry = null,
    CoreWorkspaceFactory? core = null)
{
    public async Task<int> ExecuteAsync(bool reconcile, string? intent = null, string? confirm = null,
        string? rationale = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (reconcile)
            {
                CommandError.Write(renderers, Console.Error, output,
                    "Unsupported: native reconciliation is retired. Core proposal status is execution authority; inspection cannot settle, replay or clear uncertainty.");
                return exit = 1;
            }
            var normalized = OutputFormats.Normalize(output);
            if (normalized is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("Supported output formats: human, json, json-full, json-compact, minimal."));
                return exit = 2;
            }
            if (configuration.ResolveWorkItemProviderKind() == "github")
                return exit = await InspectGitHubAsync(output, ct).ConfigureAwait(false);
            IReadOnlyList<ConnectionRemoteWriteInspection> reports =
                await reconciliation.InspectAsync(configuration, paths, ct).ConfigureAwait(false);
            reports = reports.Select(report => report with
            {
                CanReconcile = false,
                NextSteps = ["Correlated native transport evidence only. Core proposal status is execution authority; inspection does not settle, replay or clear fences."],
            }).ToArray();
            if (normalized is "json" or "json-full" or "json-compact")
            {
                if (reconcile)
                    Console.Out.WriteLine(JsonSerializer.Serialize(reports[0], TwigJsonContext.Default.ConnectionRemoteWriteInspection));
                else
                    Console.Out.WriteLine(JsonSerializer.Serialize(reports.ToArray(), TwigJsonContext.Default.ConnectionRemoteWriteInspectionArray));
            }
            else if (normalized == "minimal")
            {
                foreach (var report in reports)
                {
                    Console.Out.WriteLine($"write\t{report.IntentId}\t{report.State}\t{report.Digest}\t{report.IdentityId}");
                    foreach (var blocker in report.Blockers) Console.Out.WriteLine($"blocker\t{report.IntentId}\t{blocker}");
                    foreach (var step in report.NextSteps) Console.Out.WriteLine($"next\t{report.IntentId}\t{step}");
                }
            }
            else
            {
                var sections = new List<RenderNode>();
                foreach (var report in reports)
                {
                    var rows = new List<RenderNode>
                    {
                        new RenderNode.Text($"  original identity: {report.IdentityId}"),
                        new RenderNode.Text($"  binding: {report.BindingId}"),
                        new RenderNode.Text($"  effect: {report.Request.EffectKind}"),
                        new RenderNode.Text($"  digest: {report.Digest}"),
                    };
                    rows.AddRange(report.Blockers.Select(x => (RenderNode)new RenderNode.Text("  blocker: " + x)));
                    rows.AddRange(report.NextSteps.Select(x => (RenderNode)new RenderNode.Hint(x)));
                    sections.Add(new RenderNode.Section($"Native write {report.IntentId}: {report.State}", rows));
                }
                if (reports.Count == 0) sections.Add(new RenderNode.Text("No native remote-write intents are recorded for this checkout."));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree(sections));
            }
            return exit = !reconcile || reports[0].Receipt is not null ? 0 : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or AdoException or ArgumentException)
        {
            CommandError.Write(renderers, Console.Error, output, ex.Message);
            return exit = 1;
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, reconcile ? "connection reconcile-write" : "connection writes", output, exit, started); }
            catch (Exception) { Trace.TraceWarning("Optional command telemetry was unavailable."); }
        }
    }

    private async Task<int> InspectGitHubAsync(string output, CancellationToken ct)
    {
        if (core is null)
            throw new InvalidOperationException("GitHub transport inspection requires its pinned Core workspace; no ADO fallback.");
        if (!GitHubWorkspaceScope.TryCreate(configuration.GitHub, out var scope, out var problem))
            throw new InvalidOperationException(problem);
        var document = await ReadBoundedAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName),
            CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument, ct).ConfigureAwait(false);
        var opened = await core.OpenAsync(ct).ConfigureAwait(false);
        if (!opened.IsSuccess) throw new InvalidOperationException(opened.Error.Message);
        using var handle = opened.Value;
        if (document.Connection != handle.Workspace.Connection || document.PrincipalId != handle.Workspace.NativeActor)
            throw new InvalidOperationException("Recorded GitHub transport authority changed during inspection.");
        var captured = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!captured.IsSuccess) throw new InvalidOperationException(captured.Error.Message);
        var directory = Path.Combine(paths.TwigDir, "core-github-intents");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Redirected GitHub transport evidence directories are refused.");
        var files = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Take(201).ToArray() : [];
        if (files.Length > 200 || files.Any(path => !path.EndsWith(".json", StringComparison.Ordinal)
            && !path.EndsWith(".json.ack", StringComparison.Ordinal)))
            throw new InvalidOperationException("GitHub transport evidence exceeds the bound or has unknown files; nothing was settled.");
        var intents = files.Where(path => path.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (intents.Length > 100)
            throw new InvalidOperationException("GitHub transport inspection supports at most 100 original intents.");
        if (files.Any(path => path.EndsWith(".ack", StringComparison.Ordinal)
            && !files.Contains(path[..^4], StringComparer.Ordinal)))
            throw new InvalidOperationException("An orphan GitHub acknowledgement has no original intent; nothing was settled.");
        var rows = new List<RenderNode>();
        var unknown = false;
        foreach (var file in intents)
        {
            var intent = await ReadBoundedAsync(file, CoreGitHubNativeJsonContext.Default.CoreGitHubNativeIntent, ct).ConfigureAwait(false);
            if (intent.StoreInstance != document.DatabaseInstance || intent.Connection != document.Connection
                || intent.Actor != document.PrincipalId || intent.AcquisitionContext != document.Connection.IdentityRef
                || intent.Attempt == Guid.Empty || Path.GetFileName(file) != intent.Attempt.ToString("D") + ".json"
                || intent.Operation < 0 || intent.Method != "POST" || intent.MediaType != "application/json"
                || intent.RequestHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(intent.Payload))))
                throw new InvalidOperationException("GitHub transport intent identity/hash is invalid; original uncertainty remains.");
            var proposal = captured.Value.Proposals.SingleOrDefault(value => value.Digest == intent.Digest);
            var journal = captured.Value.Journal.SingleOrDefault(value => value.Digest == intent.Digest && value.Operation == intent.Operation);
            if (proposal is null || journal is null || journal.Attempt != intent.Attempt
                || intent.Operation >= proposal.Operations.Length)
                throw new InvalidOperationException("GitHub transport intent is not correlated with the original Core digest/ordinal/attempt.");
            var projected = GitHubWriteIntent.Project(proposal, intent.Operation, intent.Attempt);
            if (!projected.IsSuccess || projected.Value.Payload != intent.Payload
                || scope!.GraphQlEndpoint.AbsoluteUri != intent.Target)
                throw new InvalidOperationException("GitHub transport request differs from the retained Core proposal.");
            var ackPath = file + ".ack";
            var hasAck = files.Contains(ackPath, StringComparer.Ordinal);
            int? status = null;
            if (hasAck)
            {
                var ack = await ReadBoundedAsync(ackPath, CoreGitHubNativeJsonContext.Default.CoreGitHubNativeAcknowledgement, ct).ConfigureAwait(false);
                if (ack.RequestHash != intent.RequestHash || ack.StatusCode is < 100 or > 599 || ack.Body is null)
                    throw new InvalidOperationException("GitHub transport acknowledgement hash/status is invalid; nothing was settled.");
                status = ack.StatusCode;
            }
            else unknown = true;
            rows.Add(new RenderNode.Record("githubTransportEvidence", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["storeInstance"] = RenderCell.String(intent.StoreInstance.ToString("D")),
                ["connection"] = RenderCell.String(intent.Connection.Destination),
                ["recordedActor"] = RenderCell.String(intent.Actor),
                ["digest"] = RenderCell.String(intent.Digest),
                ["ordinal"] = RenderCell.Integer(intent.Operation),
                ["originalAttempt"] = RenderCell.String(intent.Attempt.ToString("D")),
                ["requestHash"] = RenderCell.String(intent.RequestHash),
                ["state"] = RenderCell.String(hasAck ? "correlated-transport-acknowledgement" : "unknown-transport-outcome"),
                ["statusCode"] = RenderCell.String(status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                ["coreJournalState"] = RenderCell.String(journal.Status.ToString()),
                ["authority"] = RenderCell.String("Correlated transport evidence only. Core proposal status is execution authority; no settlement, replay or fence change."),
            }));
        }
        if (rows.Count == 0)
            rows.Add(new RenderNode.Text("No GitHub transport intents recorded in this pinned workspace; no execution outcome is inferred."));
        renderers.GetRenderer(output).Render(new RenderTree.RenderTree(rows));
        return unknown ? 1 : 0;
    }

    private static async Task<T> ReadBoundedAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Redirected transport evidence files are refused.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 4 * 1024 * 1024) throw new JsonException("Transport evidence exceeds the read bound.");
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int read;
        while ((read = await file.ReadAsync(block, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > 4 * 1024 * 1024) throw new JsonException("Transport evidence exceeds the read bound.");
            buffer.Write(block, 0, read);
        }
        using var parsed = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        Unique(parsed.RootElement);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object
            || type.Properties.Any(property => !parsed.RootElement.TryGetProperty(property.Name, out _)))
            throw new JsonException("Transport evidence is missing required recorded members.");
        return JsonSerializer.Deserialize(buffer.ToArray(), type) ?? throw new JsonException("Transport evidence is absent.");

        static void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate transport evidence member.");
                    Unique(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }
}
