using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig config &lt;key&gt; [&lt;value&gt;]</c>: read or write configuration values.
/// </summary>
public sealed class ConfigCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine)
{
    public async Task<int> ExecuteAsync(string key, string? value = null, string outputFormat = "human")
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        _ = hintEngine; // No registered hints for config

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
            _ => null,
        };
    }
}
