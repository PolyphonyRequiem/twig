using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Serialization;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Guided preservation of one already-admitted ADO store; never initializes another provider.</summary>
internal sealed class WorkspaceMigrationCommand(
    Func<PreservationMigrationService> resolveMigration,
    TwigPaths paths,
    RendererFactory renderers,
    ITelemetryClient? telemetry = null)
{
    public async Task<int> ExecuteAsync(string? confirm = null, bool rollback = false,
        string? cancelIntent = null, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if ((rollback && cancelIntent is not null)
                || ((rollback || cancelIntent is not null) && string.IsNullOrWhiteSpace(confirm)))
            {
                CommandError.Write(renderers, Console.Error, output,
                    "Choose rollback or cancellation, and supply the exact original --confirm digest.");
                return exit = 2;
            }
            if (confirm is not null && (confirm.Length != 64 || confirm.Any(c => !Uri.IsHexDigit(c))
                || confirm != confirm.ToLowerInvariant()))
            {
                CommandError.Write(renderers, Console.Error, output,
                    "--confirm must be the exact lowercase SHA-256 digest from workspace migrate.");
                return exit = 2;
            }
            if (cancelIntent is not null && !Guid.TryParseExact(cancelIntent, "N", out _))
            {
                CommandError.Write(renderers, Console.Error, output,
                    "--cancel-intent must be the exact preparing intent ID from the migration report.");
                return exit = 2;
            }
            var target = await WorktreeLocalProviderTargetAttachmentStore.ReadObservationWithRevisionAsync(paths, ct)
                .ConfigureAwait(false);
            if (!target.IsSuccess)
                throw new InvalidOperationException(target.Error);
            if (target.Value.Envelope?.State == ProviderTargetAttachmentState.Attached)
                throw new InvalidOperationException(
                    "preservation-provider-generation-unsupported: provider-target cutover, including GitHub, requires a native storage-generation producer. An ADO mirror epoch cannot be reused.");
            if (!File.Exists(Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile)))
                throw new InvalidOperationException(
                    "preservation-admission-required: this command does not open or initialize unadmitted storage. Use the existing explicit connection-admission workflow first.");

            var migration = resolveMigration();
            PreservationMigrationResult? result = null;
            if (rollback)
                result = migration.Rollback(confirm!);
            else if (cancelIntent is not null)
                result = migration.CancelPreparation(confirm!, cancelIntent);
            else if (confirm is not null)
                result = await migration.ApplyAsync(confirm, ct).ConfigureAwait(false);
            var report = migration.Describe(result);
            if (OutputFormats.Normalize(output) is "json" or "json-full" or "json-compact")
                Console.Out.WriteLine(JsonSerializer.Serialize(report, TwigJsonContext.Default.PreservationMigrationReport));
            else
            {
                var rows = new List<RenderNode>
                {
                    new RenderNode.Text($"digest: {report.Migration.Digest}"),
                    new RenderNode.Text($"backup: {report.Migration.BackupDirectory}"),
                    new RenderNode.Text($"intent: {report.Migration.IntentId ?? "(not created)"}"),
                    new RenderNode.Text("Unsupported: unadmitted/legacy stores and provider-native cutover without a storage generation."),
                };
                rows.AddRange(report.Movements.Select(movement => (RenderNode)new RenderNode.Text(
                    $"{movement.Source} -> {movement.Destination}: {movement.Rows} rows; {movement.Effect}")));
                rows.AddRange(report.RollbackPreconditions.Select(condition =>
                    (RenderNode)new RenderNode.Text("rollback requires: " + condition)));
                rows.AddRange(report.UnsupportedWork.Select(reason =>
                    (RenderNode)new RenderNode.Text("blocked: " + reason, Severity.Error)));
                rows.AddRange(report.NextSteps.Select(step => (RenderNode)new RenderNode.Hint(step)));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([
                    new RenderNode.Section("Preservation migration: " + report.Migration.State, rows)]));
            }
            return exit = report.UnsupportedWork.Length == 0 ? 0 : 1;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException
            or UnauthorizedAccessException or JsonException or SqliteException)
        {
            CommandError.Write(renderers, Console.Error, output, error.Message);
            return exit = 1;
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, "workspace migrate", output, exit, started); }
            catch (Exception) { Trace.TraceWarning("Optional command telemetry was unavailable."); }
        }
    }
}
