using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements the <c>twig area</c> command group for managing area-path filters:
/// <c>add</c>, <c>remove</c>, <c>list</c>, <c>sync</c>, and the default area-filtered view.
/// </summary>
public sealed class AreaCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IWorkItemRepository? workItemRepo = null,
    IProcessTypeStore? processTypeStore = null,
    IIterationService? iterationService = null,
    RenderingPipelineFactory? pipelineFactory = null,
    ISprintHierarchyBuilder? sprintHierarchyBuilder = null)
{
    /// <summary>
    /// Default action: render the area-filtered workspace view.
    /// Fetches items matching configured area paths, hydrates parent chains for hierarchy
    /// context, builds the hierarchy tree, and formats the output.
    /// </summary>
    public async Task<int> ViewAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        // 1. Resolve configured area paths
        var resolved = config.Defaults.ResolveAreaPaths();
        if (resolved is null || resolved.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No area paths configured. Use 'twig area add <path>' or 'twig area sync' to configure."));
            return 0;
        }

        if (workItemRepo is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Cannot show area view: no local cache available."));
            return 1;
        }

        // 2. Convert to AreaPathFilter list for repository query
        var filters = resolved
            .Select(r => new AreaPathFilter(r.Path, r.IncludeChildren))
            .ToList();

        // 3. Query items matching area paths from local cache
        var areaItems = await workItemRepo.GetByAreaPathsAsync(filters, ct);
        var matchCount = areaItems.Count;

        if (matchCount == 0)
        {
            var areaView = AreaView.Build(areaItems, filters, matchCount: 0);
            if (renderer is not null)
                await renderer.RenderAreaViewAsync(areaView, ct);
            else
                Console.WriteLine(fmt.FormatAreaView(areaView));
            return 0;
        }

        // 4. Hydrate parent chains for hierarchy context
        var areaItemIds = areaItems.Select(i => i.Id).ToHashSet();

        var uniqueParentIds = new HashSet<int>();
        foreach (var item in areaItems)
        {
            if (item.ParentId.HasValue && !areaItemIds.Contains(item.ParentId.Value))
                uniqueParentIds.Add(item.ParentId.Value);
        }

        var parentLookup = new Dictionary<int, Domain.Aggregates.WorkItem>();
        foreach (var parentId in uniqueParentIds)
        {
            var chain = await workItemRepo.GetParentChainAsync(parentId, ct);
            foreach (var chainItem in chain)
            {
                if (!areaItemIds.Contains(chainItem.Id))
                    parentLookup.TryAdd(chainItem.Id, chainItem);
            }
        }

        // 5. Build hierarchy with IsSprintItem = IsInArea
        SprintHierarchy? hierarchy = null;
        if (processTypeStore is not null)
        {
            var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
            if (processConfig is not null)
            {
                var typeNameSet = new HashSet<string>(areaItems.Select(i => i.Type.Value), StringComparer.OrdinalIgnoreCase);

                var ceilingTypeNames = CeilingComputer.Compute(new List<string>(typeNameSet), processConfig);
                var typeLevelMap = BacklogHierarchyService.GetTypeLevelMap(processConfig);

                // Merge area items + parent context into full parent lookup
                var fullParentLookup = new Dictionary<int, Domain.Aggregates.WorkItem>(parentLookup);
                foreach (var item in areaItems)
                    fullParentLookup.TryAdd(item.Id, item);

                hierarchy = sprintHierarchyBuilder!.Build(areaItems, fullParentLookup, ceilingTypeNames, typeLevelMap);
            }
        }

        // 6. Build and render area view
        var view = AreaView.Build(areaItems, filters, hierarchy, matchCount);
        if (renderer is not null)
        {
            await renderer.RenderAreaViewAsync(view, ct);
        }
        else
        {
            Console.WriteLine(fmt.FormatAreaView(view));
        }
        return 0;
    }

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

        var semantics = entry.SemanticsLabel;
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
            Console.WriteLine(fmt.FormatInfo($"{entry.Path}  ({entry.SemanticsLabel})"));
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
            Console.WriteLine(fmt.FormatInfo($"{entry.Path}  ({entry.SemanticsLabel})"));

        Console.WriteLine(fmt.FormatSuccess($"Synced {teamAreas.Count} area path(s) from team settings."));
        return 0;
    }
}
