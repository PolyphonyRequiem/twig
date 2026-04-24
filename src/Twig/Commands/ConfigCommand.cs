using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig config &lt;key&gt; [&lt;value&gt;]</c>: read or write configuration values.
/// </summary>
public sealed class ConfigCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null)
{
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
            Console.WriteLine(fmt.FormatInfo(current));
            return 0;
        }

        // Write mode
        if (!config.SetValue(key, value))
        {
            Console.Error.WriteLine(fmt.FormatError($"Unknown or invalid configuration key/value: '{key}' = '{value}'."));
            return 1;
        }

        await config.SaveAsync(paths.ConfigPath);
        Console.WriteLine(fmt.FormatSuccess($"Set {key} = {value}"));

        // Regenerate prompt state when display settings change (badge, color, icons)
        if (key.StartsWith("display.", StringComparison.OrdinalIgnoreCase))
            if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
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
            "git.branchtemplate" => config.Git.BranchTemplate,
            "git.branchpattern" => config.Git.BranchPattern,
            "git.defaulttarget" => config.Git.DefaultTarget,
            "git.project" => config.Git.Project,
            "git.repository" => config.Git.Repository,
            "flow.autoassign" => config.Flow.AutoAssign,
            "flow.autosaveondone" => config.Flow.AutoSaveOnDone.ToString(),
            "flow.offerprondone" => config.Flow.OfferPrOnDone.ToString(),
            "display.fillratethreshold" => config.Display.FillRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "display.maxextracolumns" => config.Display.MaxExtraColumns.ToString(),
            "display.columns.workspace" => config.Display.Columns?.Workspace is { Count: > 0 } ws ? string.Join(";", ws) : null,
            "display.columns.sprint" => config.Display.Columns?.Sprint is { Count: > 0 } sp ? string.Join(";", sp) : null,
            "display.cachestaleminutes" => config.Display.CacheStaleMinutes.ToString(),
            _ => null,
        };
    }
}
