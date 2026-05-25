using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig config &lt;key&gt; [&lt;value&gt;]</c>: read or write configuration values.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// read emits a "configValue" record, write emits a "configSet" record.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class ConfigCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public async Task<int> ExecuteAsync(string key, string? value = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig config <key> [<value>]"));
            return 2;
        }

        if (value is null)
        {
            // Read mode
            var current = GetValue(key);
            if (current is null)
            {
                Console.Error.WriteLine(fmt.FormatError($"Unknown configuration key: '{key}'."));
                return 1;
            }
            RenderValueRead(key, current, outputFormat);
            return 0;
        }

        // Write mode
        if (!config.SetValue(key, value))
        {
            Console.Error.WriteLine(fmt.FormatError($"Unknown or invalid configuration key/value: '{key}' = '{value}'."));
            return 1;
        }

        await config.SaveSplitAsync(paths);
        RenderValueSet(key, value, outputFormat);

        if (key.StartsWith("display.", StringComparison.OrdinalIgnoreCase))
            if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    private void RenderValueRead(string key, string current, string outputFormat)
    {
        var tree = BuildReadTree(key, current, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderValueSet(string key, string value, string outputFormat)
    {
        var tree = BuildSetTree(key, value, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildReadTree(string key, string current, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(current),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("configValue", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["key"] = RenderCell.String(key),
                    ["value"] = RenderCell.String(current),
                }),
            _ => new RenderNode.Text(current, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderTree.RenderTree BuildSetTree(string key, string value, string outputFormat)
    {
        var message = $"Set {key} = {value}";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("configSet", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["key"] = RenderCell.String(key),
                    ["value"] = RenderCell.String(value),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Success),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private string? GetValue(string dotPath)
    {
        return dotPath.ToLowerInvariant() switch
        {
            "organization" => config.Organization,
            "project" => config.Project,
            "team" => config.Team,
            "auth.method" => config.Auth.Method,
            "defaults.areapath" => config.Defaults.AreaPath,
            "defaults.iterationpath" => config.Defaults.IterationPath,
            "seed.staledays" => config.Seed.StaleDays.ToString(),
            "display.hints" => config.Display.Hints.ToString(),
            "display.treedepth" => config.Display.TreeDepth.ToString(),
            "display.treedepthup" => config.Display.TreeDepthUp.ToString(),
            "display.treedepthdown" => config.Display.TreeDepthDown.ToString(),
            "display.treedepthsideways" => config.Display.TreeDepthSideways.ToString(),
            "display.icons" => config.Display.Icons,
            "user.name" => config.User.DisplayName,
            "user.email" => config.User.Email,
            "git.branchpattern" => config.Git.BranchPattern,
            "git.project" => config.Git.Project,
            "git.repository" => config.Git.Repository,
            "display.fillratethreshold"=> config.Display.FillRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "display.maxextracolumns" => config.Display.MaxExtraColumns.ToString(),
            "display.columns.workspace" => config.Display.Columns?.Workspace is { Count: > 0 } ws ? string.Join(";", ws) : null,
            "display.columns.sprint" => config.Display.Columns?.Sprint is { Count: > 0 } sp ? string.Join(";", sp) : null,
            "display.cachestaleminutes" => config.Display.CacheStaleMinutes.ToString(),
            "tracking.cleanuppolicy" => config.Tracking.CleanupPolicy,
            "defaults.areapathentries" or "areas.paths" => config.Defaults.AreaPathEntries is { Count: > 0 } entries
                ? string.Join(";", entries.Select(e => e.IncludeChildren ? e.Path : $"{e.Path}:exact"))
                : null,
            "areas.mode" => config.Areas.EffectiveMode,
            "workspace.worklevel" or "workspace.working_level" => config.Workspace.WorkingLevel,
            _ => null,
        };
    }
}
