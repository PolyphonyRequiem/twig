using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig config status-fields</c>: generates a status-fields configuration
/// file, opens it in the user's editor, and persists the result to <c>.twig/status-fields</c>.
/// After a successful workspace save, writes back to the global profile store (FR-08).
/// </summary>
public sealed class ConfigStatusFieldsCommand(
    IFieldDefinitionStore fieldDefinitionStore,
    IEditorLauncher editorLauncher,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IGlobalProfileStore globalProfileStore,
    TwigConfiguration config,
    ITelemetryClient? telemetryClient = null)
{
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(outputFormat, ct);
        telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "config-status-fields",
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        }, new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
        });
        return exitCode;
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
            Console.WriteLine(fmt.FormatInfo("Configuration cancelled."));
            return 0;
        }

        await File.WriteAllTextAsync(paths.StatusFieldsPath, edited, ct);

        var entries = StatusFieldsConfig.Parse(edited);
        var count = entries.Count(e => e.IsIncluded);

        Console.WriteLine(fmt.FormatSuccess($"Saved {count} field(s) to .twig/status-fields."));

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

                Console.WriteLine(fmt.FormatSuccess($"Saved field preferences globally for {org}/{process}."));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // FR-09: Write-back failure must not affect command result
        }

        return 0;
    }
}