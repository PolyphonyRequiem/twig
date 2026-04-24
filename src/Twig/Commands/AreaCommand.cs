using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements the <c>twig area</c> command group for managing area-path filters:
/// <c>add</c>, <c>remove</c>, <c>list</c>, and <c>sync</c>.
/// </summary>
public sealed class AreaCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IIterationService? iterationService = null)
{
    /// <summary>Add an area path to the workspace configuration.</summary>
    public async Task<int> AddAsync(string path, bool exact = false, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var parseResult = AreaPath.Parse(path);
        if (!parseResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError($"Invalid area path: {parseResult.Error}"));
            return 2;
        }

        config.Defaults.AreaPathEntries ??= [];

        // Duplicate check (case-insensitive)
        var existing = config.Defaults.AreaPathEntries
            .FindIndex(e => string.Equals(e.Path, parseResult.Value.Value, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Area path '{parseResult.Value.Value}' is already configured."));
            return 1;
        }

        var entry = new AreaPathEntry
        {
            Path = parseResult.Value.Value,
            IncludeChildren = !exact
        };

        config.Defaults.AreaPathEntries.Add(entry);
        await config.SaveAsync(paths.ConfigPath, ct);

        var semantics = exact ? "exact" : "under";
        Console.WriteLine(fmt.FormatSuccess($"Added area path '{entry.Path}' ({semantics})."));
        return 0;
    }

    /// <summary>Remove an area path from the workspace configuration.</summary>
    public async Task<int> RemoveAsync(string path, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (config.Defaults.AreaPathEntries is not { Count: > 0 })
        {
            Console.Error.WriteLine(fmt.FormatError("No area paths configured."));
            return 1;
        }

        var index = config.Defaults.AreaPathEntries
            .FindIndex(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Area path '{path}' is not configured."));
            return 1;
        }

        config.Defaults.AreaPathEntries.RemoveAt(index);
        await config.SaveAsync(paths.ConfigPath, ct);

        Console.WriteLine(fmt.FormatSuccess($"Removed area path '{path}'."));
        return 0;
    }

    /// <summary>List all configured area paths with their match semantics.</summary>
    public Task<int> ListAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        _ = ct; // reserved for future use
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var entries = config.Defaults.AreaPathEntries;

        if (entries is not { Count: > 0 })
        {
            Console.WriteLine(fmt.FormatInfo("No area paths configured."));
            return Task.FromResult(0);
        }

        foreach (var entry in entries)
        {
            var semantics = entry.IncludeChildren ? "under" : "exact";
            Console.WriteLine(fmt.FormatInfo($"{entry.Path}  ({semantics})"));
        }

        Console.WriteLine(fmt.FormatInfo($"{entries.Count} area path(s) configured."));
        return Task.FromResult(0);
    }

    /// <summary>Fetch team area paths from ADO and replace the current configuration.</summary>
    public async Task<int> SyncAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (iterationService is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Cannot sync area paths: not connected to Azure DevOps."));
            return 1;
        }

        IReadOnlyList<(string Path, bool IncludeChildren)> teamAreas;
        try
        {
            teamAreas = await iterationService.GetTeamAreaPathsAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Failed to fetch team area paths: {ex.Message}"));
            return 1;
        }

        if (teamAreas.Count == 0)
        {
            Console.Error.WriteLine(fmt.FormatError("No team area paths found in ADO."));
            return 1;
        }

        config.Defaults.AreaPathEntries = teamAreas
            .Select(a => new AreaPathEntry { Path = a.Path, IncludeChildren = a.IncludeChildren })
            .ToList();

        await config.SaveAsync(paths.ConfigPath, ct);

        foreach (var entry in config.Defaults.AreaPathEntries)
        {
            var semantics = entry.IncludeChildren ? "under" : "exact";
            Console.WriteLine(fmt.FormatInfo($"{entry.Path}  ({semantics})"));
        }

        Console.WriteLine(fmt.FormatSuccess($"Synced {teamAreas.Count} area path(s) from team settings."));
        return 0;
    }
}
