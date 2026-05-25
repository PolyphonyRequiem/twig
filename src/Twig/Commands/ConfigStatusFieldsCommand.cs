using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig config status-fields</c>: generates a status-fields configuration
/// file, opens it in the user's editor, and persists the result to <c>.twig/status-fields</c>.
/// After a successful workspace save, writes back to the global profile store (FR-08).
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// emits "statusFieldsCancelled", "statusFieldsSaved", and optionally
/// "statusFieldsSavedGlobally" records. <see cref="OutputFormatterFactory"/> is
/// retained only for stderr error formatting.
/// </remarks>
public sealed class ConfigStatusFieldsCommand(
    IFieldDefinitionStore fieldDefinitionStore,
    IEditorLauncher editorLauncher,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IGlobalProfileStore globalProfileStore,
    TwigConfiguration config,
    ITelemetryClient? telemetryClient = null,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("config-status-fields", outputFormat);
        int exitCode;
        try
        {
            exitCode = await ExecuteCoreAsync(outputFormat, ct);
            scope.Complete(exitCode);
            telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "config-status-fields",
                ["exit_code"] = exitCode.ToString(),
                ["output_format"] = outputFormat,
                ["twig_version"] = VersionHelper.GetVersion(),
                ["os_platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
            }, new Dictionary<string, double>
            {
                ["duration_ms"] = Stopwatch.GetElapsedTime(scope.StartTimestamp).TotalMilliseconds
            });
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> ExecuteCoreAsync(string outputFormat, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var definitions = await fieldDefinitionStore.GetAllAsync(ct);
        if (definitions.Count == 0)
        {
            Console.Error.WriteLine(fmt.FormatError("No field definitions cached. Run 'twig sync' first."));
            return 1;
        }

        var existingContent = File.Exists(paths.StatusFieldsPath)
            ? await File.ReadAllTextAsync(paths.StatusFieldsPath, ct)
            : null;

        var content = StatusFieldsConfig.Generate(definitions, existingContent, config.ProcessTemplate);

        var edited = await editorLauncher.LaunchAsync(content, ct);
        if (edited is null)
        {
            RenderCancelled(outputFormat);
            return 0;
        }

        await File.WriteAllTextAsync(paths.StatusFieldsPath, edited, ct);

        var entries = StatusFieldsConfig.Parse(edited);
        var count = entries.Count(e => e.IsIncluded);

        var nodes = new List<RenderNode>(2)
        {
            BuildSavedNode(count, paths.StatusFieldsPath, outputFormat),
        };

        // FR-08: Write-back to global profile. FR-09: Silent on failure.
        try
        {
            if (!string.IsNullOrEmpty(config.Organization) && !string.IsNullOrEmpty(config.ProcessTemplate))
            {
                var org = config.Organization;
                var process = config.ProcessTemplate;

                var hash = FieldDefinitionHasher.ComputeFieldHash(definitions);

                await globalProfileStore.SaveStatusFieldsAsync(org, process, edited, ct);

                var now = DateTimeOffset.UtcNow;
                var existingMeta = await globalProfileStore.LoadMetadataAsync(org, process, ct);
                var metadata = new ProfileMetadata(
                    Organization: org,
                    CreatedAt: existingMeta?.CreatedAt ?? now,
                    LastSyncedAt: now,
                    FieldDefinitionHash: hash,
                    FieldCount: definitions.Count);

                await globalProfileStore.SaveMetadataAsync(org, process, metadata, ct);

                nodes.Add(BuildSavedGloballyNode(org, process, outputFormat));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // FR-09: Write-back failure must not affect command result
        }

        var tree = new RenderTree.RenderTree(nodes);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        return 0;
    }

    private void RenderCancelled(string outputFormat)
    {
        const string message = "Configuration cancelled.";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("statusFieldsCancelled", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static RenderNode BuildSavedNode(int count, string path, string outputFormat)
    {
        var message = $"Saved {count} field(s) to .twig/status-fields.";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("statusFieldsSaved", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["fieldCount"] = RenderCell.Integer(count),
                    ["path"] = RenderCell.String(path),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Success),
        };
    }

    private static RenderNode BuildSavedGloballyNode(string org, string process, string outputFormat)
    {
        var message = $"Saved field preferences globally for {org}/{process}.";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("statusFieldsSavedGlobally", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["organization"] = RenderCell.String(org),
                    ["processTemplate"] = RenderCell.String(process),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Success),
        };
    }
}