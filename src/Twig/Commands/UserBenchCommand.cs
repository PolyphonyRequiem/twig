using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Telemetry;
using Twig.Rendering;
using Twig.RenderTree;
using Twig.Store.Sqlite;

namespace Twig.Commands;

internal sealed class UserBenchCommand(UserBenchStore store, RendererFactory renderers, ITelemetryClient? telemetry = null,
    CoreWorkspaceFactory? factory = null)
{
    internal async Task<int> ExecuteAsync(string action, string? name, string output, CancellationToken ct,
        string? newName = null, string? confirm = null, string? expectBench = null, string? expectContents = null,
        bool management = false)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(output);
        var exit = 1;
        try { return exit = await Execute(); }
        catch (OperationCanceledException) { return exit = Error("Bench management was cancelled; refresh before retrying."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return exit = Error("Bench management could not complete: " + ex.Message); }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, "bench " + action, format ?? "invalid", exit, started); }
            catch (Exception) { Debug.WriteLine("Optional Bench telemetry failed; command outcome was preserved."); }
        }

        int Error(string message, int code = 1)
        {
            CommandError.Write(renderers, Console.Error, format ?? OutputFormats.Default, message);
            return code;
        }
        void Result(string kind, Bench bench, string message)
            => renderers.GetRenderer(format!).Render(new RenderTree.RenderTree([new RenderNode.Record(kind,
                new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.String(bench.Id.ToString("D")), ["name"] = RenderCell.String(bench.Name),
                    ["revision"] = RenderCell.Integer(bench.Revision), ["message"] = RenderCell.String(message),
                })]));

        async Task<int> Execute()
        {
            if (format is null) return Error($"Unsupported output format. Use {OutputFormats.Describe()}.", 2);
            if (format == "ids" && action != "list") return Error("IDs output is supported only by Bench listing.", 2);
            if (management && format is not ("json" or "json-full" or "json-compact"))
                return Error("--include-management requires JSON output.", 2);
            if (expectBench is not null && !Guid.TryParseExact(expectBench, "D", out _))
                return Error("--expect-bench requires a canonical Bench GUID.", 2);
            if (expectContents is not null && (expectContents.Length != 64 || !expectContents.All(Uri.IsHexDigit)))
                return Error("--expect-contents requires the complete contents digest from a management listing.", 2);
            Guid? current = null;
            if (action == "list" && factory is not null)
            {
                var opened = await factory.OpenAsync(ct);
                if (!opened.IsSuccess) return Error(opened.Error.Message);
                using var handle = opened.Value;
                var snapshot = await handle.Workspace.BenchSnapshotAsync(ct);
                if (!snapshot.IsSuccess) return Error(snapshot.Error.Message);
                var context = await handle.Workspace.Context.ReadAsync(ct);
                if (!context.IsSuccess) return Error(context.Error.Message);
                current = context.Value.BenchId;
            }
            var read = await store.ReadAsync(ct);
            if (!read.IsSuccess) return Error(read.Error.Message);
            if (action == "list")
            {
                if (format == "ids")
                {
                    foreach (var bench in read.Value.Benches) Console.WriteLine(bench.Id.ToString("D"));
                    return 0;
                }
                if (management)
                {
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject(); writer.WriteNumber("version", 3);
                        writer.WriteString("storeId", read.Value.InstanceId); writer.WriteNumber("revision", read.Value.Revision);
                        if (current is { } selected) writer.WriteString("currentBenchId", selected); else writer.WriteNull("currentBenchId");
                        writer.WriteStartArray("benches");
                        foreach (var bench in read.Value.Benches)
                        {
                            writer.WriteStartObject(); writer.WriteString("id", bench.Id); writer.WriteString("name", bench.Name);
                            writer.WriteNumber("revision", bench.Revision); writer.WriteString("contentsDigest", ContentsDigest(bench));
                            writer.WritePropertyName("definition"); writer.WriteRawValue(BenchCodec.Serialize(bench));
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
                    return 0;
                }
                var rows = read.Value.Benches.Select(bench => new RenderRow("bench", new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.String(bench.Id.ToString("D")), ["name"] = RenderCell.String(bench.Name),
                    ["revision"] = RenderCell.Integer(bench.Revision), ["selectorCount"] = RenderCell.Integer(bench.Selectors.Length),
                    ["isCurrent"] = RenderCell.Boolean(current == bench.Id),
                })).ToArray();
                renderers.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Table("User-wide Benches",
                    [new("name", "Bench"), new("id", "ID"), new("isCurrent", "Current"), new("revision", "Revision"), new("selectorCount", "Pins")], rows)]));
                return 0;
            }
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
                return Error("An explicit trimmed Bench name or GUID is required.", 2);
            var matches = read.Value.Benches.Where(bench => bench.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || bench.Id.ToString("D").Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (action == "create")
            {
                if (matches.Length != 0) return Error("A Bench with that name already exists; nothing was created or selected.");
                var created = await store.SaveAsync(new(Guid.NewGuid(), name, []), 0, ct);
                if (!created.IsSuccess) return Error(created.Error.Message, created.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
                Result("benchCreated", created.Value, $"Created user-wide Bench '{created.Value.Name}' without selecting it.");
                return 0;
            }
            if (matches.Length != 1) return Error("The requested Bench is absent or ambiguous; no replacement was adopted.");
            var target = matches[0];
            if (expectBench is not null && expectBench != target.Id.ToString("D"))
                return Error("The captured Bench identity changed; no replacement under the same name was adopted.");
            if (expectContents is not null && expectContents != ContentsDigest(target))
                return Error("The captured Bench contents changed; review its current pins before retrying.");
            if (action == "rename")
            {
                if (string.IsNullOrWhiteSpace(newName)) return Error("An explicit new Bench name is required.", 2);
                var renamed = await store.SaveAsync(new(target.Id, newName, target.Selectors), target.Revision, ct);
                if (!renamed.IsSuccess) return Error(renamed.Error.Message, renamed.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
                Result("benchRenamed", renamed.Value, $"Renamed the shared Bench to '{renamed.Value.Name}'; selections and pins were preserved.");
                return 0;
            }
            if (action != "delete") return Error("Unknown Bench management operation.", 2);
            if (!target.Selectors.IsEmpty && confirm != target.Name)
                return Error($"This Bench holds {target.Selectors.Count(pin => pin.Kind == SelectorKind.Pin)} item pin(s), "
                    + $"{target.Selectors.Count(pin => pin.Kind == SelectorKind.Subtree)} tree pin(s) and "
                    + $"{target.Selectors.Count(pin => pin.Kind == SelectorKind.Query)} query rule(s). "
                    + "Re-type its exact name with --confirm; no work items or drafts will be deleted.");
            var deleted = await store.DeleteAsync(target.Id, target.Revision, ct);
            if (!deleted.IsSuccess) return Error(deleted.Error.Message);
            Result("benchDeleted", new(target.Id, target.Name, target.Selectors, target.Revision + 1),
                $"Deleted Bench '{target.Name}'; work items, drafts and other Benches were untouched.");
            return 0;
        }
    }

    internal static string ContentsDigest(Bench bench)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BenchCodec.Serialize(bench))));
}
